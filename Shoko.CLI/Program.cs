// ******************************* Module Header *******************************
// Module Name:   Program.cs
// Project:       Shoko.CLI
// 
// MIT License
// 
// Copyright © 2020 Shoko Suite
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
// *****************************************************************************

using System;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NLog;
using NLog.Extensions.Logging;
using Shoko.CLI.Exceptions;
using Shoko.CLI.Properties;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Shoko.CLI
{
    /// <summary>
    ///     The command line interface for shoko server.
    /// </summary>
    [PublicAPI]
    public class Program
    {
        private static IHostBuilder CreateBuilder(string[] args)
        {
            //Set up the default host builder with args.
            //This already includes
            //*) set the ContentRootPath to the result of GetCurrentDirectory()
            //*) load host IConfiguration from "DOTNET_" prefixed environment variables
            //*) load host IConfiguration from supplied command line args
            //*) load app IConfiguration from 'appsettings.json' and 'appsettings.[EnvironmentName].json'
            //*) load app IConfiguration from User Secrets when EnvironmentName is 'Development' using the entry assembly
            //*) load app IConfiguration from environment variables
            //*) load app IConfiguration from supplied command line args
            //*) configure the ILoggerFactory to log to the console, debug, and event source output
            //*) enables scope validation on the dependency injection container when EnvironmentName is 'Development'
            IHostBuilder builder = Host.CreateDefaultBuilder(args)
                                       .ConfigureLogging((context, loggingBuilder) =>
                                                         {
                                                             //Use NLog instead of default logger.
                                                             loggingBuilder.ClearProviders();
                                                             loggingBuilder.AddNLog(new NLogLoggingConfiguration(context.Configuration.GetSection("NLog")));
                                                         });
            return builder;
        }

        private static IHostBuilder CreateHostBuilder(string[] args)
        {
            string? instance = null;
            for (var x = 0; x < args.Length - 1; x++)
            {
                if (!args[x].Equals("instance", StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                instance = args[x + 1];
                break;
            }

            var arguments = new ProgramArguments {Instance = instance};
            return CreateBuilder(args)
                .ConfigureServices((hostContext, services) =>
                                   {
                                       services.AddSingleton(arguments);
                                       services.AddHostedService<Worker>();
                                   });
        }

        /// <summary>
        ///     Entry Point for the command line interface.
        /// </summary>
        /// <param name="args">
        ///     Arguments given at startup.
        /// </param>
        /// <remarks>
        ///     Arguments can be used define the instance or to override environment variables
        /// </remarks>
        public static async Task Main([NotNull] string[] args)
        {
            //For logging with NLog to work inside of Main it is essential that ShutdownOnDispose is turned off.
            //Otherwise the catch Messages will not show up in the Log.
            //The downside is that Shutdown() of NLog has to be called explicitly if we use the AsyncWrapper for logging.
            //There is no harm in calling it anyway so it is done every time.
            ILogger? logger = null;
            try
            {
                using IHost host = CreateHostBuilder(args).Build();
                logger = host.Services.GetRequiredService<ILogger<Program>>();
                logger.LogInformation(Resources.Program_Main_HostCreated_LogMessage);
                // here we would do things that need to happen after init, but before shutdown, while blocking the main thread.
                await host.RunAsync();
            }
            catch (ShokoServerNotRunningException e)
            {
                logger?.LogError(e, Resources.Program_Main_ShokoServerNotRunning_LogMessage);
            }
            catch (Exception e)
            {
                logger?.LogCritical(e, Resources.Program_Main_UnexpectedError_LogMessage);
            }
            finally
            {
                //Explicit call to NLog to ensure every log is written in case of async logging.
                LogManager.Shutdown();
            }
        }
    }
}
