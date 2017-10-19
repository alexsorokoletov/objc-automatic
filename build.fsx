// include Fake lib
#r @"./packages/auto/FAKE/tools/FakeLib.dll"
#r @"./packages/auto/FSharp.Data/lib/net40/FSharp.Data.dll"
#r @"./packages/auto/Newtonsoft.Json/lib/net40/Newtonsoft.Json.dll"

open Fake
open System.IO
open FSharp.Data
open Fake.NuGetHelper
open System
open System.Collections
open System.Collections.Generic
open System.Net
open System.Text.RegularExpressions
open Newtonsoft.Json

let podsFolder = "pods/" 
let bindingsFolder = "bindings/"
let packagesDirUnsorted = "packages-raw"
let packagesDirSorted = "packages-good"
let packagesSourceName = "DT.Automatic"
let packagesTempDir = "packages-tmp"
let packagesTargetFramework = "Xamarin.iOS"

let sampleCSProject = "csproject.template.xml"
let sampleAssemblyInfo = "assemblyinfo.template.xml"
let samplePackagesConfig = "packages.template.xml"
let sampleNuspec = "nuspec.template.xml"
let sampleXCodeProject = "xcode.template"
let sampleLinkerFile = "linker.template.cs"

let rootNamespace = "DreamTeam.Xamarin."
let rootPackageName = "DT.Xamarin."
let defaultPodVersion = ""
let rn = Environment.NewLine
let fwRegex = @"(?<fwname>[^/.]+).framework"
let mutable isVerboseOutput = false
let firstRegexMatch input (regex:string) = 
     Regex.Matches(input, regex)
        |> Seq.cast<Match>
        |> Seq.head
        |> fun m -> m.Groups.Item(1)
        |> fun g -> g.Value
let (|Prefix|_|) (p:string) (s:string) =
    if s.StartsWith(p) then
        Some(s.Substring(p.Length))
    else
        None

let freshDirectory d = 
    DeleteDir d
    CreateDir d

let execProcess setup =
     let processOutLog = if isVerboseOutput then trace else ignore
     let logError = if isVerboseOutput then traceError else ignore
     ExecProcessWithLambdas setup System.TimeSpan.MaxValue (not isVerboseOutput) logError processOutLog

let execProcessWithResult configProcessStartInfoF = 
    let errors = new List<_>()
    let messages = new List<_>()
    let exitCode = ExecProcessWithLambdas configProcessStartInfoF System.TimeSpan.MaxValue true (errors.Add) (messages.Add)
    ProcessResult.New exitCode messages errors

let unzipWithTar targetFolder zipFile =
    freshDirectory targetFolder
    let result = execProcess (fun info ->  
       info.FileName <- "tar"
       info.Arguments <- "-xzf " + zipFile + " -C " + targetFolder)
    if result <> 0 then failwithf "Error during tar -xzf  %s -C %s" zipFile targetFolder

let sbLine (line:String) (sb:System.Text.StringBuilder) =
    sb.AppendLine(line)
let traceV fmt = 
    if isVerboseOutput then tracefn else ignore

// type PodDependencyNode = {Name:string; Children:List<PodDependencyNode>}

type Podspec = JsonProvider<"./podspec.sample.json">

[<CustomEquality; CustomComparison>] 
type Pod = { name : string; 
            spec: Podspec.Root; 
            mutable dependencies: Pod list; 
            mutable custom: bool;
            mutable empty: bool; }
            
            member x.IsUmbrella = //umbrella pod is pod which has no binary but has some dependencies and at least one dependency has binary
                    let res = x.empty && not (List.isEmpty x.dependencies) && (x.dependencies |> List.exists (fun f -> not f.empty))
                    // tracefn "Checking isUmbrella on %s (%d) ==== %b (why: empty %b, dependencies %A, non-empty dependencies %A)" 
                    //         x.name (x.GetHashCode()) res x.empty
                    //         (x.dependencies |> List.map (fun f -> f.name))
                    //         (x.dependencies |> List.filter (fun f-> not f.empty) |> List.map (fun f -> f.name))
                    res 
                        
            override x.Equals(y) =
                match y with
                | :? Pod as p -> (x.name = p.name)
                | _ -> false

            override x.GetHashCode() = hash x.name

            interface System.IComparable with
                member x.CompareTo y =
                    match y with
                    | :? Pod as p -> compare x.name p.name
                    | _ -> invalidArg "y" "cannot compare values of different types"                                    


let podSpecFileName podName =
    Path.Combine(podsFolder, podName+".json")   

let podNameWithoutSubSpec (pod:string)=
    if pod.Contains("/") then pod.Substring(0, pod.IndexOf("/")) else pod 

let specForPod podName =
    let podSpecFile = podSpecFileName (podNameWithoutSubSpec podName)
    let podSpec = Podspec.Parse(File.ReadAllText podSpecFile)
    podSpec

let podSubSpec (pod:string)=
    if pod.Contains("/") then pod.Substring(pod.IndexOf("/") + 1) else "" 
            
let podByName pod =
    let spec = specForPod pod
    { name = pod; spec = spec; dependencies = []; custom=false; empty=false }


let getPodSpecUrl podName =
     "https://trunk.cocoapods.org/api/v1/pods/"+podName+"/specs/latest"
      
let downloadPodSpec (pod:string) =
    let podName = podNameWithoutSubSpec pod
    let podUrl = getPodSpecUrl podName
    let podSpecJson = (new WebClient()).DownloadString(podUrl)
    let podSpecFile = podSpecFileName podName
    File.WriteAllText(podSpecFile, podSpecJson);
    specForPod podName

let private fileSafePodName (pod:string) =
    pod.Replace("/", ".").Replace("+", "_").Replace("-","_")

let podBindingsFolder podName =
    let safePodName = fileSafePodName podName
    Path.Combine(bindingsFolder, safePodName)

let downloadPod podName =
    let safePodName = fileSafePodName podName
    let targetFolder = System.IO.Path.Combine(podsFolder, safePodName, "XCode")
    freshDirectory targetFolder
    CopyDir targetFolder sampleXCodeProject (fun _ -> true)
    let podFile = Path.Combine(targetFolder, "Podfile")
    let replacements = [ "@PodName@", podName]
    processTemplates replacements [podFile]
    let result = execProcess (fun info ->  
                                info.FileName <- "pod"
                                info.WorkingDirectory <- targetFolder
                                info.Arguments <- "install")
    if result <> 0 then failwith "Error during pod install" 
    let expandFolder = Path.Combine(targetFolder, "Pods", (podNameWithoutSubSpec podName))
    expandFolder    

let private alwaysArray (json: JsonValue) propertyName = 
    let j = json.TryGetProperty propertyName
    if j.IsSome then
        match j.Value with
            | JsonValue.String single -> [|single|]
            | JsonValue.Array array -> array |> Array.map (fun a -> a.AsString())
            | _ -> [||]
    else
        [||]


let isFrameworkSpec (podSpec: Podspec.Root) =
    let vendoredFrameworkOptional = podSpec.JsonValue.TryGetProperty "vendored_frameworks"
    vendoredFrameworkOptional.IsSome 

let generateLinkWith pod =
   let podName = pod.name
   let podSpec = pod.spec
   let safePodName = fileSafePodName podName 
   let isFramework = isFrameworkSpec podSpec
   let linkTarget = if isFramework then
                        let frameworks = alwaysArray podSpec.JsonValue "vendored_frameworks"
                        Path.GetFileName(frameworks.[0])
                    else
                        safePodName + ".a"
   
   let linkWithContent = new System.Text.StringBuilder()
   linkWithContent.AppendLine("using ObjCRuntime;") |> ignore
   linkWithContent.AppendFormat(@"[assembly: LinkWith (""{0}"", {1}", linkTarget, Environment.NewLine) |> ignore
   let podFrameworks = alwaysArray podSpec.JsonValue "frameworks"
   if (podFrameworks.Length > 0) then
        linkWithContent.AppendFormat(@"Frameworks = ""{0}"",{1}", String.Join(" ", podFrameworks), Environment.NewLine) |> ignore
   let libraries = alwaysArray podSpec.JsonValue "libraries"
   if not <| Array.isEmpty libraries then
        let linkerFlags = libraries 
                            |> Seq.cast<string>
                            |> Seq.map (fun s -> "-l"+s)
                            |> Seq.toArray
        linkWithContent.AppendFormat(@"LinkerFlags = ""{0}"",{1}", String.Join(" ", linkerFlags), Environment.NewLine) |> ignore
   linkWithContent.AppendLine(@"IsCxx = true,") |> ignore
   linkWithContent.AppendLine(@"SmartLink = true,") |> ignore
   linkWithContent.AppendLine(@"ForceLoad = true)]") |> ignore
   linkWithContent.ToString();


let podHeadersLocation (podName:string) = 
    let truePodName = podNameWithoutSubSpec podName
    let podSubSpec = podSubSpec podName
    let podSpec = specForPod podName
    let podFolder = fileSafePodName podName
    let isFramework = isFrameworkSpec podSpec
    if isFramework then
        let frameworks = alwaysArray podSpec.JsonValue "vendored_frameworks"
        let fwName = frameworks.[0]
        Path.Combine(podsFolder, podFolder, "XCode", "Pods", truePodName, fwName, "Headers")
    else 
        let guessFrameworkPath = Path.Combine(podsFolder, podFolder, "XCode", "Pods", truePodName, truePodName) 
        let bestGuess = Path.Combine(guessFrameworkPath, truePodName + ".h")
        if File.Exists bestGuess then
            guessFrameworkPath
        else
            traceFAKE "Can't get headers location for a pod that is not providing framework %s" podName        
            ""

let podFrameworkLocation pod = 
    let podName = pod.name
    let truePodName = podNameWithoutSubSpec podName
    let podSubSpec = podSubSpec podName
    let podSpec = pod.spec
    let podFolder = fileSafePodName podName
    let isFramework = isFrameworkSpec podSpec
    if isFramework then
        let frameworks = alwaysArray podSpec.JsonValue "vendored_frameworks"
        let fwName = frameworks.[0]
        let fwContainerDir = firstRegexMatch fwName fwRegex
        " -F " + Path.Combine(podsFolder, podFolder, "XCode", "Pods", truePodName, fwContainerDir)
    else 
        let guessFrameworkPath = Path.Combine(podsFolder, podFolder, "XCode", "Pods", truePodName) 
        let bestGuess = Path.Combine(guessFrameworkPath, truePodName, truePodName + ".h")
        if File.Exists bestGuess then
            " -I " + guessFrameworkPath
        else
            traceFAKE "Did not guess framework location for a pod that is not providing framework %s" podName        
            ""

let podDependenciesSimple (pod:Pod) =
    let subSpec = podSubSpec pod.name
    if String.IsNullOrEmpty subSpec then
        let dependenciesOptional = pod.spec.JsonValue.TryGetProperty "dependencies"
        if dependenciesOptional.IsSome then
            dependenciesOptional.Value.Properties() 
                |> Seq.map (fun (k,v) -> k)
                |> Seq.toList
        else
            []
    else    
        let podSubSpec = pod.spec.Subspecs |> Seq.pick (fun s -> if s.Name = subSpec then Some(s) else None )
        let dependenciesOptional = podSubSpec.JsonValue.TryGetProperty "dependencies"
        if dependenciesOptional.IsSome then
                    dependenciesOptional.Value.Properties() 
                        |> Seq.map (fun (k,v) -> k)
                        |> Seq.toList
                else
                    []

let podDependencies (pod:Pod) =
    podDependenciesSimple pod |> Seq.map (fun k -> podByName k) |> Seq.toList   

let getDependentHeadersLocations pod =
    let podName = pod.name
    let podSpec = pod.spec
    let dependencyHeaders = new System.Text.StringBuilder()
    let dependencies = podDependencies pod |> List.append [pod]
    match dependencies with 
        | [] -> [||]
        | _ ->  dependencies |> Seq.filter(fun subPod ->
                                            let path = podHeadersLocation subPod.name
                                            Directory.Exists(path)
                                           )    
                                        |> Seq.map podFrameworkLocation
                                        |> Seq.toArray               

let getDependenciesUsings pod = 
    let usings = new System.Text.StringBuilder()
    let dependencies = pod.dependencies |> List.filter (fun p -> not p.empty) |> List.sort
    dependencies |> Seq.iter(fun subPod ->
                                let podNamespace = rootNamespace + fileSafePodName subPod.name
                                usings.AppendFormat("using {0};{1}", podNamespace, Environment.NewLine) |> ignore    
                            )    
    usings.ToString()               

let copyBinaryAndGenerateLinkWith pod podExpandedFolder =
    let podName = pod.name
    let safePodName = fileSafePodName podName 
    let podSpec = pod.spec
    let frameworks = alwaysArray podSpec.JsonValue "vendored_frameworks"
    let fwName = frameworks.[0]
    let fwFolderPath = Path.Combine(podExpandedFolder, fwName)
    let bindingFolder = podBindingsFolder podName
    let targetFolder = Path.Combine(bindingFolder, fwName);
    freshDirectory bindingFolder
    freshDirectory targetFolder
    let linkWithFile = Path.Combine(bindingFolder, safePodName + ".linkwith.cs");
    let linkerSuggestions = generateLinkWith pod
    File.WriteAllText(linkWithFile, linkerSuggestions)
    CopyDir targetFolder fwFolderPath (fun _ -> true)    

let fixShapieBugs structsFile apiDefinitionFile =
    //workaround for bug https://bugzilla.xamarin.com/show_bug.cgi?id=46360
    let baseTypes = [" : nint", " : long"
                     " : nuint", " : ulong" 
                    ]
    processTemplates baseTypes [structsFile]
let generateCSharpBindingsForFramework pod podExpandedFolder =
    let podName = pod.name
    let podSpec = pod.spec
    let bindingFolder = podBindingsFolder podName
    let frameworks = alwaysArray podSpec.JsonValue "vendored_frameworks"
    let fwName = frameworks.[0]  
    tracefn "FW NAME IS %A" fwName
    let fwBinaryName = firstRegexMatch fwName fwRegex
    let iosSdkInSharpie = @"""iphoneos""" 
    let headersFolder = Path.Combine(podExpandedFolder, fwName, "Headers")
    let apiDefinitionFile = Path.Combine(bindingFolder, "ApiDefinitions.cs")
    let structsAndEnumsFile = Path.Combine(bindingFolder, "StructsAndEnums.cs")
    if Directory.Exists(headersFolder) then
        let rootHeaderFile = Path.Combine(headersFolder, fwBinaryName + ".h")
        let allHeaders = Path.Combine(headersFolder, "*.h")
        let podNamespace = rootNamespace + fileSafePodName podName
        let dependenciesHeaders = getDependentHeadersLocations pod
        let depHeadersOption = if dependenciesHeaders.Length > 0 then (" " + String.Join(" ", dependenciesHeaders )) else "" 
        let sharpieArgs =  "bind -output " + bindingFolder + " -sdk "+ iosSdkInSharpie + " -scope " + allHeaders + " " 
                            + rootHeaderFile + " -n " + podNamespace + " -c -I" + headersFolder +  depHeadersOption + " -v"
        let result = execProcess (fun info ->  
                                    info.FileName <- "sharpie"
                                    info.Arguments <- sharpieArgs)
        if result <> 0 then failwithf "Error during sharpie %s " sharpieArgs
        if not <| File.Exists(structsAndEnumsFile) then File.WriteAllText(structsAndEnumsFile, "")
        let additionalUsings = getDependenciesUsings pod
        fixShapieBugs structsAndEnumsFile apiDefinitionFile
        if not <| String.IsNullOrWhiteSpace additionalUsings then
            let apiDefinition = File.ReadAllText(apiDefinitionFile)
            let apiWithUsings = additionalUsings + apiDefinition
            //TODO in this case we should not add using clause to parent pod
            File.WriteAllText(apiDefinitionFile, apiWithUsings)
    else
        [ apiDefinitionFile;structsAndEnumsFile] |> Seq.iter (fun f-> File.WriteAllText(f, ""))
        printfn "Empty files generated for API definition since there are no headers in %s" podName 


let generateCSharpBindingsForCustom pod =
    let podName = pod.name
    let bindingFolder = podBindingsFolder podName
    let safePodName = fileSafePodName podName
    let truePodName = podNameWithoutSubSpec podName
    let podXCodeDir = Path.Combine(podsFolder, safePodName, "XCode")
    let buildOutDir = Path.Combine(podXCodeDir, "build-out")
    let iosSdkInSharpie = @"""iphoneos""" 
    let headersFolder = buildOutDir
    let possibleUmbrellaHeader = Path.Combine(headersFolder, truePodName + ".h")
    let mainHeader = if File.Exists possibleUmbrellaHeader then
                        possibleUmbrellaHeader  
                      else
                        let allHeaders = Path.Combine(headersFolder, "*.h")
                        let allHeaderFiles = !! allHeaders |> Seq.toArray //expand wildcard otherwise ExecProcess fails
                        let allHeadersString = String.Join(" ", allHeaderFiles)
                        allHeadersString

    let podPrivate = "-I " + Path.Combine(podXCodeDir, "Pods", "Headers", "Private")
    let podPublic = "-I" + Path.Combine(podXCodeDir, "Pods", "Headers", "Public")
    let dependenciesHeaders = [podPrivate; podPublic] |> Seq.toArray
    let depHeadersOption = if dependenciesHeaders.Length > 0 then (" " + String.Join(" ", dependenciesHeaders)) else "" 
    let podNamespace = rootNamespace + safePodName
    let sharpieArgs =  "bind -output " + bindingFolder + " -sdk "+ iosSdkInSharpie + " -scope " + headersFolder 
                             + " " + mainHeader 
                             + " -n " + podNamespace + " -c " +  depHeadersOption + " -v"
    let result = execProcess (fun info ->  
                                info.FileName <- "sharpie"
                                info.Arguments <- sharpieArgs)
    if result <> 0 then failwithf "Error during sharpie %s " sharpieArgs
    let apiDefinitionFile = Path.Combine(bindingFolder, "ApiDefinitions.cs")
    let structsAndEnumsFile = Path.Combine(bindingFolder, "StructsAndEnums.cs")
    if not <| File.Exists(structsAndEnumsFile) then File.WriteAllText(structsAndEnumsFile, "")
    fixShapieBugs structsAndEnumsFile apiDefinitionFile

let getPackageVersionForPod (podSpec: Podspec.Root) =
    let realVersion = (podSpec.JsonValue.GetProperty "version").AsString()
    let overrideVersion = getBuildParamOrDefault "VERSION" defaultPodVersion
    let suffix = getBuildParamOrDefault "VERSION-TYPE" "" 
    if not <| String.IsNullOrEmpty(overrideVersion) then overrideVersion + suffix else realVersion + suffix

let buildCSProjReferences podDependencies =
    let references = new System.Text.StringBuilder();
    podDependencies |> List.filter (fun pod -> not pod.empty) 
                    |> List.iter (fun pod ->
                                    let subPod = pod.name 
                                    let dllName = fileSafePodName subPod
                                    let packageName = rootPackageName + fileSafePodName subPod
                                    let subPodSpec = pod.spec
                                    let subPodVersion =  getPackageVersionForPod subPodSpec
                                    references.AppendFormat(@"    <Reference Include=""{0}"">{1}", dllName, rn) |> ignore
                                    references.AppendFormat(@"      <HintPath>..\{0}\{1}.{2}\lib\{3}\{4}.dll</HintPath>{5}", packagesTempDir, packageName, subPodVersion, packagesTargetFramework, dllName, rn) 
                                        |> sbLine "      <Private>True</Private>" |> sbLine "    </Reference>" |> ignore 
                                 )
    references.ToString()

let checkPodForResources pod podPath bindingFolder =
    //TODO check resource from podspec first and return existing directories
    let resourcesDir = Path.Combine(podPath, "Resources") 
    if Directory.Exists resourcesDir then
        //until xs supports wildcards https://bugzilla.xamarin.com/show_bug.cgi?id=48954
        let targetFolder = Path.Combine(bindingFolder, "Resources")
        CleanDir targetFolder
        CreateDir targetFolder
        CopyDir targetFolder resourcesDir (fun _ -> true)
        let relativeDir = (DirectoryInfo resourcesDir).Parent.FullName
        let allFiles = !! (resourcesDir + "/**/*.*") |> Seq.map (fun f-> 
                                                                         if f.StartsWith(relativeDir) then
                                                                            f.Substring(relativeDir.Length+1)
                                                                         else 
                                                                            f
                                                                         )
                                                     |> Seq.map (fun f -> f.Replace("/","\\"))
                                                     |> Seq.map (fun f->
                                                                        String.Format("    <BundleResource Include=\"{0}\" />", f)
                                                                )
        printfn "Found BundleResources: %A" allFiles
        String.Join(Environment.NewLine, allFiles)    
        // "         <BundleResource Include=\"Resources\**\*\" />"
    else
        ""

let frameworkName pod =
    let vendoredFrameworkOptional = pod.spec.JsonValue.TryGetProperty "vendored_frameworks"
    let isFramework = vendoredFrameworkOptional.IsSome
    if isFramework then
        let frameworks = alwaysArray pod.spec.JsonValue "vendored_frameworks"
        let fwName = frameworks.[0]
        fwName
    else
        ""

let generateReferenceCode pod isFramework =
    let safePodName = fileSafePodName pod.name
    if isFramework then
        let frameworks = alwaysArray pod.spec.JsonValue "vendored_frameworks"
        let fwName = frameworks.[0]
        let ref = "  <ItemGroup>"  + Environment.NewLine
                + "    <ObjcBindingNativeFramework Include=\"" + fwName + "\" />"  + Environment.NewLine
                + "  </ItemGroup>"  + Environment.NewLine
                + "  <ItemGroup>" + Environment.NewLine
                + "    <Compile Include=\"" + safePodName + ".linkwith.cs\">" + Environment.NewLine
                + "    </Compile>" + Environment.NewLine
                + "  </ItemGroup>" + Environment.NewLine
        ref
    else
        "  <ItemGroup>" + Environment.NewLine  
        + "    <ObjcBindingNativeLibrary Include=\"" + safePodName + ".a\" />" + Environment.NewLine
        + "  </ItemGroup>" + Environment.NewLine
        + "  <ItemGroup>" + Environment.NewLine
        + "    <Compile Include=\"" + safePodName + ".linkwith.cs\">" + Environment.NewLine
        + "        <DependentUpon>" + safePodName + ".a</DependentUpon>" + Environment.NewLine
        + "    </Compile>" + Environment.NewLine
        + "  </ItemGroup>" + Environment.NewLine
    

let generateCSProject (pod: Pod) podExpandedFolder isFramework =
    let podName = pod.name
    let podSpec = pod.spec
    let safePodName = fileSafePodName podName
    let podNamespace = rootNamespace + safePodName
    let dependencies = pod.dependencies
    let references = buildCSProjReferences dependencies
    let bindingFolder = podBindingsFolder podName
    let dllName = safePodName
    let nuspecFile = Path.Combine(bindingFolder, safePodName + ".nuspec")
    CopyFile nuspecFile sampleNuspec
    let linkerFile = Path.Combine(bindingFolder, "Linker.cs")
    CopyFile linkerFile sampleLinkerFile
    if not <| pod.IsUmbrella then
        let bundleResources = checkPodForResources pod podExpandedFolder bindingFolder
        let binaryReference = generateReferenceCode pod isFramework
        let replacements = 
                [   "@ProjectGuid@", Guid.NewGuid().ToString("B").ToUpper()
                    "@RootNamespace@", podNamespace
                    "@AssemblyName@", safePodName
                    "@PodName@", safePodName
                    "@References@", references
                    "@BinaryReference@", binaryReference
                    "@BundleResources@", bundleResources
                    "@Description@", "Xamarin binding ===" + podSpec.Description.Replace("\r"," ").Replace("\n"," ") ]
        let csProjectFile = Path.Combine(bindingFolder, dllName + ".csproj")
        let assemblyInfoFolder = Path.Combine(bindingFolder, "Properties")
        let assemblyInfoFile = Path.Combine(assemblyInfoFolder,  "AssemblyInfo.cs")
        CreateDir assemblyInfoFolder 
        CopyFile csProjectFile sampleCSProject
        CopyFile assemblyInfoFile sampleAssemblyInfo
        processTemplates replacements [csProjectFile; assemblyInfoFile]
        if not <| List.isEmpty dependencies then
            let packagesConfig = Path.Combine(bindingFolder, "packages.config")
            CopyFile packagesConfig samplePackagesConfig
            let pb = new System.Text.StringBuilder()
            dependencies |> List.filter (fun subPod -> subPod.IsUmbrella || not subPod.empty)
                         |> List.iter (fun subPod ->
                                        let subPodSpec = subPod.spec
                                        let subPodVersion =  getPackageVersionForPod subPodSpec
                                        let packageName = rootPackageName + fileSafePodName subPod.name
                                        pb.AppendFormat(@"    <package id=""{0}"" version=""{1}"" targetFramework=""{2}"" />{3}", packageName, subPodVersion, packagesTargetFramework ,  Environment.NewLine) |> ignore
                                    )
            processTemplates ["@Packages@", pb.ToString() ] [packagesConfig]                    
        
    let depBuilder = new System.Text.StringBuilder();
    match dependencies with 
        | [] -> ()
        | _ ->  dependencies
                |> List.filter (fun subPod -> subPod.IsUmbrella || not subPod.empty)
                |> List.iter (fun (subPod) ->
                                let subPodSpec = subPod.spec
                                let subPodVersion =  getPackageVersionForPod subPodSpec
                                let packageName = rootPackageName + fileSafePodName subPod.name
                                depBuilder.AppendFormat(@"    <dependency id=""{0}"" version=""{1}"" />{2}", packageName, subPodVersion, Environment.NewLine) |> ignore
                             )
    let files = if pod.IsUmbrella then "" else String.Format(@"<file src=""bin/Release/{0}.dll"" target=""lib/{1}"" />", dllName, packagesTargetFramework)                              
    let nugetParams = 
            [ "@PackageId@", rootPackageName + fileSafePodName podName
              "@Version@", getPackageVersionForPod podSpec 
              "@Authors@", "DreamTeam Mobile"
              "@Summary@", podSpec.Summary
              "@Description@", "Umbrella Xamarin binding === " + podSpec.Description
              "@ProjectUrl@", podSpec.Homepage
              "@ReleaseNotes@", ""
              "@Files@", files
              "@Dependencies@", depBuilder.ToString()
               ]
    processTemplates nugetParams [nuspecFile]
    let usingsBuilder = new System.Text.StringBuilder();
    depBuilder.Clear() |> ignore;
    let methodName = "DontLooseMeDuringBuild"
    match dependencies with 
        | [] -> ()
        | _ ->  dependencies
                |> List.filter (fun subPod -> subPod.IsUmbrella || not subPod.empty) 
                |> List.iter (fun (subPod) -> 
                                let safeSubPodName =  fileSafePodName subPod.name
                                let subClassName = "___" + (rootNamespace + safeSubPodName).Replace(".","_")
                                let subPodNamespace = rootNamespace + safeSubPodName
                                usingsBuilder.AppendFormat(@"using {0};{1}", subPodNamespace, Environment.NewLine) |> ignore
                                depBuilder.AppendFormat(@"            {0}.{1}();{2}", subClassName, methodName, Environment.NewLine) |> ignore
                             )
    let linkerFileParams = 
            [ "@namespace@", podNamespace
              "@classname@", "___" + podNamespace.Replace("." , "_") 
              "@method@", methodName
              "@usings@", usingsBuilder.ToString()
              "@dependencies@", depBuilder.ToString()
               ]
    processTemplates linkerFileParams [linkerFile]
    ()

let allPodsCache : Dictionary<string,Pod> = new Dictionary<string, Pod>()
 
let rec downloadPodsRecursive (podName:string) =
    if not <| allPodsCache.ContainsKey podName then   
        let podSpec = downloadPodSpec podName
        let tempPod = {name = podName; spec = podSpec; dependencies = []; custom = false; empty = false }
        let dependencies = podDependenciesSimple tempPod
        match dependencies with 
            | [] -> ignore()
            | _ ->  dependencies |> Seq.iter downloadPodsRecursive |> ignore
        allPodsCache.Add(podName, tempPod)
    
let compileNonFrameworkProjectForArchitecure podName sim podXCodeDir buildOutDir =
    //see https://gist.github.com/madhikarma/09e553c508f870639570
    let architectureArgs = if sim then @" -arch i386 -arch x86_64 -sdk ""iphonesimulator""" else @" -sdk ""iphoneos"""
    let xcodeArgs = @"clean build -workspace EmptyProject.xcworkspace -scheme EmptyProject"
                    + architectureArgs
                    + @" CODE_SIGN_IDENTITY="""" CODE_SIGNING_REQUIRED=NO"
                    + " ONLY_ACTIVE_ARCH=NO"
                    + " SYMROOT=" + buildOutDir 
                    + " CONFIGURATION_BUILD_DIR=" + buildOutDir
    let result = execProcess (fun info ->  
       info.FileName <- "xcodebuild"
       info.WorkingDirectory <- podXCodeDir
       info.Arguments <- xcodeArgs)
    if result <> 0 then failwithf "Error during xcodebuild %s" xcodeArgs
    let architectureExt = if sim then ".sim.a" else ".device.a"
    let binaryFile = "lib" + podNameWithoutSubSpec podName + ".a"
    let binaryFinalFile = "lib" + podNameWithoutSubSpec podName + architectureExt
    let binaryPath = Path.Combine(buildOutDir, binaryFile)
    if File.Exists binaryPath then
        let binaryFinalPath  = Path.Combine(buildOutDir, binaryFinalFile)
        CopyFile binaryFinalPath binaryPath
        binaryFinalPath
    else
        traceFAKE "VERIFY: pod %s doesn't have any binaries generated for config %s" podName architectureArgs
        ""

let createUniversalLib file1 file2 outFile =
    //see https://gist.github.com/madhikarma/09e553c508f870639570
    //lipo -create -output "${HOME}/Desktop/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" "${SRCROOT}/build/Release-iphoneos/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}" "${SRCROOT}/build/Release-iphonesimulator/${FRAMEWORK_NAME}.framework/${FRAMEWORK_NAME}"
    let lipoArgs = "-create -output " + outFile + " " + file1 + " " + file2
    let result = execProcess (fun info ->  
       info.FileName <- "lipo"
       info.Arguments <- lipoArgs)
    if result <> 0 then failwithf "Error during lipo %s" lipoArgs

let compileNonFrameworkProject pod =
    let podName = pod.name 
    let podSpec = pod.spec
    let safePodName = fileSafePodName podName
    let podXCodeDir = Path.Combine(podsFolder, safePodName, "XCode")
    let buildOutDir = Path.Combine(podXCodeDir, "build-out")
    freshDirectory buildOutDir
    let absOutDir = (DirectoryInfo buildOutDir).FullName
    let bindingFolder = podBindingsFolder podName
    freshDirectory bindingFolder
    let armPath = compileNonFrameworkProjectForArchitecure podName false podXCodeDir absOutDir
    if not <| String.IsNullOrEmpty armPath then
        let simPath = compileNonFrameworkProjectForArchitecure podName true podXCodeDir absOutDir
        let binaryFileName = "lib" + podNameWithoutSubSpec podName + ".a"
        let binaryPath = Path.Combine(buildOutDir, binaryFileName)
        createUniversalLib armPath simPath binaryPath
        let safePodName = fileSafePodName podName
        let dotAName = Path.Combine(bindingFolder, safePodName + ".a");
        CopyFile dotAName binaryPath 
        let linkWithFile = Path.Combine(bindingFolder, safePodName + ".linkwith.cs");
        let linkerSuggestions = generateLinkWith pod
        File.WriteAllText(linkWithFile, linkerSuggestions)
        true
    else
        traceFAKE "VERIFY: pod %s most probably doesn't have any binaries generated" podName
        false

let generateBindingForPod (pod:Pod) = 
    let podExpandedFolder = downloadPod pod.name
    let vendoredFrameworkOptional = pod.spec.JsonValue.TryGetProperty "vendored_frameworks"
    let isFramework = vendoredFrameworkOptional.IsSome
    pod.custom <- not <| isFramework 
    if isFramework then
        tracefn "Generating C# bindings for Framework pod %s" pod.name
        copyBinaryAndGenerateLinkWith pod podExpandedFolder
        generateCSharpBindingsForFramework pod podExpandedFolder
        pod.empty <- false
        generateCSProject pod podExpandedFolder true
    else
        tracefn "Generating C# bindings for non-Framework pod %s" pod.name
        let producesBinary = compileNonFrameworkProject pod
        pod.empty <- not producesBinary
        if pod.IsUmbrella then
            tracefn "%s is an umbrella pod" pod.name
        if producesBinary then
            generateCSharpBindingsForCustom pod 
        if producesBinary || pod.IsUmbrella then
            generateCSProject pod podExpandedFolder false

    // tracefn "Pod %s, empty %b, dependencies are %A" pod.name pod.empty (pod.dependencies |> List.map (fun f->f.name)) 
    ()

let private listIntersect l1 l2 =
    Set.intersect (Set.ofList l1) (Set.ofList l2) |> Set.toList

let private listCopy l1 =
    l1 |> Seq.ofList |> List.ofSeq


let sortGraphIteration source res = 
   let mutable resolved = listCopy res
   for pod in source do 
      if not <| List.contains pod resolved  then
        let dependencies = pod.dependencies
        let noDependencies = List.isEmpty dependencies
        let allDepsResolved = Set.isEmpty (Set.difference (Set.ofList dependencies) (Set.ofList resolved))
        if noDependencies || allDepsResolved then //resolve pod
            resolved <- List.append resolved [pod]
            ()
   resolved

let sortDependencyGraph all = //there is a better way
    let mutable resolvedPods = List.empty<Pod>
    let allCopy = all |> Seq.ofList |> List.ofSeq
    for i in all do
        resolvedPods <- sortGraphIteration allCopy resolvedPods
    resolvedPods
                 
let rec addDependentPods (list:List<Pod>) (pod:Pod) = 
    list.Add pod
    let dependencies = podDependencies pod
    dependencies |> Seq.iter (fun d -> addDependentPods list d)
    pod.dependencies <- dependencies 
    
let buildDependencyGraph podName skipDependencies=
    let pod = podByName podName
    let allPods = new List<Pod>()
    if skipDependencies then
        allPods.Add pod
    else
        addDependentPods allPods pod
    let allPodsUnique = allPods |> Seq.distinct |> List.ofSeq
    printfn "List of pods: %A" (allPodsUnique |> List.map (fun p -> p.name))
    let tree = sortDependencyGraph allPodsUnique
    allPodsCache.Clear()
    // let's remove duplicate pods and make sure we are always using same instance of pod everywhere
    let distinctTree = tree |> List.map (fun p -> 
                                                let cachedPod = if List.isEmpty p.dependencies then  p
                                                                else
                                                                    let dep = p.dependencies |> List.map (fun sp -> allPodsCache.[sp.name])
                                                                    let tempPod = {name = p.name; spec = p.spec; dependencies = dep; custom = false; empty = false }
                                                                    tempPod
                                                allPodsCache.Add(p.name, cachedPod) 
                                                cachedPod
                                        ) 
    printfn "Sorted dependency graph: %A" (distinctTree |> List.map (fun p -> p.name))
    distinctTree    

let generateBuildScript podName depGraph =
    let buildScriptFileName = Path.Combine(bindingsFolder, (fileSafePodName podName) + ".build.sh")
    let script = new System.Text.StringBuilder();
    let rawDir = packagesDirUnsorted
    let nugetSourceDir = Path.Combine(bindingsFolder, packagesDirSorted)
    Path.Combine(bindingsFolder, rawDir) |> freshDirectory 
    nugetSourceDir |> freshDirectory
    let absDirPath = (DirectoryInfo nugetSourceDir).FullName
    Path.Combine(bindingsFolder, packagesTempDir) |> freshDirectory
    script.AppendLine("#!/bin/bash") |> sbLine "set -e" 
                    |> sbLine "rm -rf packages-tmp/*.*" 
                    |> sbLine "rm -rf packages-good/*.*"
                    |> sbLine "rm -rf packages-raw/*.*"
                    |> ignore
    script.AppendFormat("nuget sources Remove -Name {0} || true {1}", packagesSourceName, rn)  |> ignore
    script.AppendFormat("nuget sources Add -Name {0} -Source {1}{2}", packagesSourceName, absDirPath, rn)  |> ignore
    depGraph |> List.iter (fun pod ->
                            let subPod = pod.name
                            script.AppendFormat(@"echo ""------------------------- Processing Binding {0}...""{1}", subPod, rn) |> ignore
                            script.AppendFormat(@"pushd {0}{1}", fileSafePodName subPod, rn) |> sbLine "rm -f *.nupkg" |>  ignore
                            if not pod.IsUmbrella then
                                script.AppendFormat(@"[ -f packages.config ] && nuget restore -source {0} -PackagesDirectory ../{1} || echo ""no restore required for this project""{2}", packagesSourceName, packagesTempDir, rn) |> ignore
                                script.AppendLine("msbuild /t:Clean") |> ignore
                                script.AppendFormat(@"msbuild /p:Configuration=Release{0}", rn) |> ignore
                            script.AppendFormat(@"nuget pack *.nuspec{0}", rn) |> ignore
                            script.AppendFormat(@"cp -f *.nupkg ../{0}/ {1}",rawDir, rn) |>  sbLine "popd" |> sbLine "nuget init packages-raw packages-good" |> ignore 
                            script.AppendFormat(@"nuget list -source {0} {1}",packagesSourceName, rn) 
                                |> sbLine "nuget init packages-raw packages-good"
                                |> ignore 
                          )
    // script.AppendFormat("nuget sources Remove -Name {0}{1}", packagesSourceName, rn) |> ignore
    if File.Exists buildScriptFileName then 
        File.Delete buildScriptFileName
    File.WriteAllText(buildScriptFileName, script.ToString())
    ""

Target "CleanPods" ( fun()->
    trace "CleanPods"
    freshDirectory podsFolder
    File.WriteAllText(Path.Combine(podsFolder, "empty.txt"), "")
);

Target "CleanBindings" ( fun()->
    trace "CleanBindings"
    freshDirectory bindingsFolder
    File.WriteAllText(Path.Combine(bindingsFolder, "empty.txt"), "")
);

Target "Bind" ( fun()->
    let verboseIsOn = hasBuildParam "VERBOSE"
    isVerboseOutput <- verboseIsOn
    tracefn "VERBOSE IS %A" isVerboseOutput
    let podName = getBuildParamOrDefault "POD" ""
    let isSingle = (getBuildParamOrDefault "SINGLE" "") = "YES"
    if String.IsNullOrEmpty(podName) then
        traceFAKE "You must specify pod name as a POD variable: sh bind.sh POD=FirebaseAnalytics"
    else
        downloadPodsRecursive podName
        let mapping = buildDependencyGraph podName isSingle
        mapping |> List.iter generateBindingForPod
        let valuablePods = mapping |> List.filter (fun p-> p.IsUmbrella || (not p.empty))
        let umbrellaPods = mapping |> List.filter ( fun p -> p.IsUmbrella)    
        let u = (umbrellaPods |> List.map (fun p -> p.name))
        printfn "Umbrella packages will be generated for these pods %A" u
        generateBuildScript podName valuablePods |> ignore
        ()
);

Target "Test" ( fun()->
    ()
);

// start build
RunTargetOrDefault("Bind")