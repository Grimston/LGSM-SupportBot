// ***********************************************************************
// Assembly         : SupportBot
// Author           : Grimston
// Created          : 01-24-2020
//
// Last Modified By : Grimston
// Last Modified On : 04-16-2020
// ***********************************************************************
// <copyright file="Worker.cs" company="NPipes">
//     Copyright (c) NPipes. All rights reserved.
// </copyright>
// <summary></summary>
// ***********************************************************************
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportBot.Data;
using SupportBot.Triggers;

namespace SupportBot
{
    /// <summary>
    /// Does all the work for the bot in the background as a service application
    /// Implements the <see cref="BackgroundService" />
    /// </summary>
    /// <seealso cref="BackgroundService" />
    public class Worker : BackgroundService
    {
        /// <summary>
        /// The logger
        /// </summary>
        private readonly ILogger<Worker> _logger;

        /// <summary>
        /// Gets or sets the database.
        /// </summary>
        /// <value>The database.</value>
        public static LiteDatabase Database { get; set; }

        /// <summary>
        /// Gets or sets the settings.
        /// </summary>
        /// <value>The settings.</value>
        public static BotSettings Settings { get; set; }

        /// <summary>
        /// Gets or sets the discord socket.
        /// </summary>
        /// <value>The discord socket.</value>
        public DiscordSocketClient DiscordSocket { get; set; }

        /// <summary>
        /// The application path
        /// </summary>
        public readonly string AppPath;

        public IConfiguration Config { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="Worker"/> class.
        /// </summary>
        /// <param name="logger">The logger.</param>
        /// <param name="configuration"></param>
        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger;
            Config = configuration;

            AppPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.GetDirectoryName(Assembly.GetEntryAssembly().Location) : "/srv/LinuxGSMbot/LinuxGSMbot";

            Database = new LiteDatabase(Path.Combine(AppPath, "bot.db"));
            var settingStorage = Database.GetCollection<BotSettings>("settings");
            if (settingStorage.Count() == 0)
            {
                //The defaults here are the support channels (from 2020)
                settingStorage.Insert(new BotSettings() { Name = "LinuxGSM Support Bot", AllowedChannels = new ulong[] { 140667754586832896, 135126471319617536, 425089610477993984, 424152809970204672, 219535041468956673 } });
            }
            Settings = settingStorage.FindAll().OrderBy(x => x.Id).Last();

            UpdateTriggers();
        }

        /// <summary>
        /// execute as an asynchronous operation.
        /// </summary>
        /// <param name="stoppingToken">Triggered when <see cref="M:Microsoft.Extensions.Hosting.IHostedService.StopAsync(System.Threading.CancellationToken)" /> is called.</param>
        /// <returns>A <see cref="T:System.Threading.Tasks.Task" /> that represents the long running operations.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var services = ConfigureServices();
                DiscordSocket = services.GetRequiredService<DiscordSocketClient>();

                DiscordSocket.Log += Log;

                await DiscordSocket.LoginAsync(TokenType.Bot, (string)Config.GetValue(typeof(string), "BotToken"));
                await DiscordSocket.StartAsync();

                await services.GetRequiredService<CommandHandler>().InstallCommandsAsync();

                DiscordSocket.MessageReceived += MessageReceived;

                await Task.Delay(-1, stoppingToken);
                Database.Dispose();
            }
        }

        public static void UpdateTriggers()
        {
            try
            {
                using var client = new WebClient();
                var triggers = System.Text.Json.JsonSerializer.Deserialize<BotTrigger>(client.DownloadString(
                    "https://raw.githubusercontent.com/Grimston/LGSM-SupportBot/master/SupportBot/triggers.json"));
                if (triggers == null) return;

                var triggerCollection = Database.GetCollection<Trigger>();
                triggerCollection.DeleteAll(); //Remove everything
                triggerCollection.InsertBulk(triggers.Triggers);
            }
            catch (Exception)
            {
                Debug.Print("Failed to update Triggers!");
                //Usually this is a temporary issue with GitHub.
            }
        }

        /// <summary>
        /// Handles all received messages.
        /// </summary>
        /// <param name="message">The message.</param>
        private async Task MessageReceived(SocketMessage message)
        {
            if (message.Author.IsBot)
            {
                return;
            }

            if (!Settings.AllowedChannels.Contains(message.Channel.Id))
            {
                return;
            }

            try
            {
                var userRoles = (message.Author as SocketGuildUser)?.Roles;

                if (HasIgnoreRole(userRoles, message))
                {
                    return;
                }

            }
            catch (Exception)
            {
                //Not a guild message?
            }


            var content = message.Content.ToLower();

            var availableTriggers = new List<Trigger>();

            foreach (var item in Database.GetCollection<Trigger>().FindAll())
            {
                var canHandle = false;
                foreach (var starter in item.Starters)
                {
                    try
                    {
                        canHandle = (starter.Type.ToLower()) switch
                        {
                            "regex" => Regex.IsMatch(content, starter.Value),
                            _ => content.Contains(starter.Value),
                        };
                    }
                    catch (Exception)
                    {
                        //Ignore it, probably something wrong with the regex..
                    }

                    if (canHandle)
                    {
                        break;
                    }
                }

                if (canHandle)
                {
                    availableTriggers.Add(item);
                }
            }

            if (availableTriggers.Count != 0)
            {
                if (availableTriggers.Count > 1)
                {
                    var combinedAnswers = availableTriggers.Aggregate(
                        "Multiple potential issues found, all answers found:\n",
                        (current, item) => current + $"{item.Answer}\n"
                        );

                    await message.Channel.SendMessageAsync(combinedAnswers);
                }
                else
                {
                    await message.Channel.SendMessageAsync(availableTriggers[0].Answer);
                }
            }

            //TODO: regex for these

            //if (content.Contains("cronjob") || content.Contains("cron"))
            //{
            //    await message.Channel.SendMessageAsync(strings.Cronjob);
            //    return;
            //}

            //if ((content.Contains("rust") && content.Contains("custom map")))
            //{
            //    await message.Channel.SendMessageAsync(strings.RustCustomMap);
            //    return;
            //}
        }

        /// <summary>
        /// Determines whether [has ignore role] [the specified user roles].
        /// </summary>
        /// <param name="userRoles">The user roles.</param>
        /// <param name="message">The message.</param>
        /// <returns><c>true</c> if [has ignore role] [the specified user roles]; otherwise, <c>false</c>.</returns>
        private bool HasIgnoreRole(IEnumerable<SocketRole> userRoles, IMessage message)
        {
            //I... don't remember what this channel is...
            if (message.Channel.Id == 425089610477993984)
            {
                return false;
            }

            return userRoles.Any(item => Config.GetValue<ulong[]>("IgnoredRoles").Contains(item.Id));
        }

        /// <summary>
        /// Logs the specified MSG.
        /// </summary>
        /// <param name="msg">The MSG.</param>
        /// <returns>Task.</returns>
        private Task Log(LogMessage msg)
        {
            switch (msg.Severity)
            {
                case LogSeverity.Critical:
                    _logger.LogCritical(new EventId(600, msg.Source), msg.Exception, msg.Message);
                    break;
                case LogSeverity.Error:
                    _logger.LogError(new EventId(500, msg.Source), msg.Exception, msg.Message);
                    break;
                case LogSeverity.Warning:
                    _logger.LogWarning(new EventId(400, msg.Source), msg.Exception, msg.Message);
                    break;
                case LogSeverity.Verbose:
                    _logger.LogTrace(new EventId(300, msg.Source), msg.Exception, msg.Message);
                    break;
                case LogSeverity.Debug:
                    _logger.LogDebug(new EventId(200, msg.Source), msg.Exception, msg.Message);
                    break;
                default:
                    _logger.LogInformation(new EventId(100, msg.Source), msg.Exception, msg.Message);
                    break;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Configures services.
        /// </summary>
        /// <returns>ServiceProvider.</returns>
        private ServiceProvider ConfigureServices()
        {
            return new ServiceCollection()
                .AddSingleton<DiscordSocketClient>()
                .AddSingleton<CommandService>()
                .AddSingleton<CommandHandler>()
                .AddSingleton<HttpClient>()
                .BuildServiceProvider();
        }
    }
}
