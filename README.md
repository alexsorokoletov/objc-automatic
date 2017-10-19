## Automatic way of binding Cocoapods for Xamarin iOS

[![Join the chat at https://gitter.im/objc-automatic/Lobby](https://badges.gitter.im/objc-automatic/Lobby.svg)](https://gitter.im/objc-automatic/Lobby?utm_source=badge&utm_medium=badge&utm_campaign=pr-badge&utm_content=badge)

Automatic conversion from cocoapod to nuget (saving the hierarchy).
For example, the tool provides "automatic" way to bind all the pods for Firebase iOS for Xamarin.
Project is in beta state, expect all nuget packages of Firebase in the Nuget feed soon in alpha channel.

This tool can help with Cocoapods written in Objective-C. At this time, `objc-automatic` doesn't support Swift-based pods.

Tool works as a two step process. First you generate the bindings automatically (via bind.sh ) then you compile the bindings and package then as nuget.

To some extent this tool is similar to Xamarin's *Objective Sharpie*. Biggest difference is that this tool works with CocoaPod hierachy and generates set of dependent packages with Xamarin.iOS bindings mimicking hierarchy of CocoaPod

### How to use
Blog post explaining whole story: [Easy way to create Xamarin.iOS binding from CocoaPods](http://sorokoletov.com/2017/02/05/objc-automatic-easy-way-to-create-xamarin-bindings-ios/)

**Video 1. objc-automatic generating lottie-ios binding for Xamarin.iOS**

https://www.useloom.com/share/e17d23d0ec3711e689c9c95ad27ead9a

**Video 2. objc-automatic demo - binding Firebase/Analytics to Xamarin**

https://www.useloom.com/share/7679bec0ec3911e6b8fde34d395e0c71

**Video 3. objc-automatic overview and details how it works internally**

https://www.useloom.com/share/f756df30ec3b11e6b8fde34d395e0c71

Prerequisites:
- CocoaPods ([Getting started guide](https://guides.cocoapods.org/using/getting-started.html)). One might need `pod try yourpodname` at least once to force CocoaPods to update local specs repo. 
- run `mozroots --import --sync` to import SSL root certificates.
- NuGet 3. To update NuGet, run `sudo nuget update -Self` 
- Xamarin iOS 
- ObjectiveSharpie latest (3.4). [Download information and manual](https://developer.xamarin.com/guides/cross-platform/macios/binding/objective-sharpie/).

**First stage - generate bindings**

To generate bindings `sh bind.sh POD=LMGaugeView` or `sh bind.sh POD=Firebase/Messaging` or use other pod names (see here https://firebase.google.com/docs/ios/setup in _Available Pods_ section)

Other options:
- POD=podName
- VERSION=version (override versions in all packages --- use with caution and for tests)
- SINGLE=yes  - do not take into consideration pod references
- VERSION-TYPE=-beta (adds suffix to version)
- VERBOSE - to enable verbose output

**Second stage - compile and package as nuget**
To compile bindings and package then, run `sh LMGaugeView.build.sh` script in the `bindings` folder. This script is generated automatically and will show any errors in the bindings you need to correct.

After 2nd stage you should have a set of nuget packages ready for publishing in folder [bindings/packages-raw](https://github.com/alexsorokoletov/Xamarin.Firebase.iOS/tree/dev/automatic/bindings/packages-raw)

Between 1st and 2nd stages you might need to check generated bindings and make sure they compile. With git diff this is usually quick and easy.

### Building generated bindings
1. To build all bindings use `sh {pod name}.build.sh` script in `bindings` folder. 
1. To compile single binding use `msbuild -p:Configuration=Release` to compile in Release in the specific binding folder (e.g. bindings/AWSCore)

#### Additional options
Clean bindings folder - `sh bind.sh CleanBindings`

Clean pods folder - `sh bind.sh CleandPods`

#### Things to fix and improve 
1. detailed nuspec packages info and support for sub-version updates
1. support weak_frameworks linker flag
1. way to generate multiple pods at once
1. support for iOS flavored dependencies (see ZendeskSDK pod)
1. video showing how to use this 

#### Contribution
Please ping me if you want to collaborate, extend, fix or change the project. I will be happy to sort out the process and answer any questions

#### Extra
Please see beginning of the build.fsx file for configuration options (verbose logs, versions, etc)
