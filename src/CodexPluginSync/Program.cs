using System;

namespace CodexPluginSync
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                return Cli.CliRunner.Run(args);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Fatal: " + ex.GetType().Name + ": " + ex.Message);
                return Cli.ExitCodes.Error;
            }
        }
    }
}
