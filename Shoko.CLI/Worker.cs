// ******************************* Module Header *******************************
// Module Name:   Worker.cs
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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.CLI.Exceptions;
using Shoko.CLI.Properties;
using Shoko.Server.Server;
using Shoko.Server.Settings;
using Shoko.Server.Utilities;

namespace Shoko.CLI
{
    /// <summary>
    ///     This is a background service worker used to handle a shoko server instance.
    /// </summary>
    public sealed class Worker : BackgroundService
    {
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ProgramArguments _args;
        private readonly ILogger<Worker> _logger;

        /// <summary>
        ///     Initializes a new instance of the <see cref="Worker" /> class.
        /// </summary>
        /// <param name="args">
        ///     A <see cref="ProgramArguments" /> that holds specific settings for the worker.
        /// </param>
        /// <param name="lifetime">
        ///     A <see cref="IHostApplicationLifetime" /> that allows consumers to be notified of application lifetime events.
        /// </param>
        /// <param name="logger">
        ///     A <see cref="ILogger{Worker}" /> instance for logging purposes.
        /// </param>
        public Worker(ProgramArguments args, IHostApplicationLifetime lifetime, ILogger<Worker> logger)
        {
            // Make sure everything is set.
            _args = args ?? throw new ArgumentNullException(nameof(args));
            _appLifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        //async without await is useless. We return Task instead.
        /// <inheritdoc />
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (!string.IsNullOrEmpty(_args.Instance))
            {
                ServerSettings.DefaultInstance = _args.Instance;
            }

            ShokoServer.Instance.InitLogger();

            ServerSettings.LoadSettings();
            ServerState.Instance.LoadSettings();
            if (!ShokoServer.Instance.StartUpServer())
            {
                return Task.FromException(new ShokoServerNotRunningException());
            }

            // Ensure that the AniDB socket is initialized. Try to Login, then start the server if successful.
            ShokoServer.Instance.RestartAniDBSocket();
            if (!ServerSettings.Instance.FirstRun)
            {
                ShokoServer.RunWorkSetupDB();
            }
            else
            {
                _logger.LogWarning(Resources.Worker_ExecuteAsync_ServerNotStarted_LogMessage);
            }

            ShokoServer.Instance.ServerShutdown += (sender, eventArgs) => _appLifetime.StopApplication();
            Utils.YesNoRequired += (sender, e) => e.Cancel = true;

            ServerState.Instance.PropertyChanged += (sender, e) =>
                                                    {
                                                        if (e.PropertyName == "StartupFailedMessage" && ServerState.Instance.StartupFailed)
                                                        {
                                                            // Changed from Console to logger output.
                                                            // Console output can configured in the logger if a console is present.
                                                            _logger.LogError(Resources.Worker_ExecuteAsync_StartupFailed_LogMessage, ServerState.Instance.StartupFailedMessage);
                                                        }
                                                    };
            // Changed from Console to logger output.
            // Console output can configured in the logger if a console is present.
            ShokoService.CmdProcessorGeneral.OnQueueStateChangedEvent +=
                ev => _logger.LogInformation(Resources.Worker_ExecuteAsync_GeneralQueueStateChange_LogMessage, ev.QueueState.formatMessage());

            //Everything is fine so we return a completed task.
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            ShokoService.CancelAndWaitForQueues();
            await base.StopAsync(cancellationToken);
        }
    }
}
