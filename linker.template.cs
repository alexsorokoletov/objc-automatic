@usings@

namespace @namespace@
{
    public static class @classname@
    {
        static @classname@()
        {
            @method@();
        }

        public static void @method@()
        {
@dependencies@
        }
    }
}

namespace ApiDefinitions
{
	partial class Messaging
	{
		static Messaging()
		{
			@namespace@.@classname@.@method@();
		}
	}
}