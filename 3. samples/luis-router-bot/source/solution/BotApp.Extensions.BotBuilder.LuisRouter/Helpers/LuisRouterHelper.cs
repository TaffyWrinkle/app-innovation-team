﻿using BotApp.Extensions.BotBuilder.LuisRouter.Accessors;
using BotApp.Extensions.BotBuilder.LuisRouter.Domain;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.AI.Luis;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace BotApp.Extensions.BotBuilder.LuisRouter.Helpers
{
    public class LuisRouterHelper : BaseHelper
    {
        private readonly LuisRouterConfig config = null;

        public LuisRouterHelper(string environmentName, string contentRootPath)
        {
            var builder = new ConfigurationBuilder()
              .SetBasePath(contentRootPath)
              .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
              .AddJsonFile($"appsettings.{environmentName}.json", optional: true)
              .AddEnvironmentVariables();

            var configuration = builder.Build();

            config = new LuisRouterConfig();
            configuration.GetSection("LuisRouterConfig").Bind(config);
        }

        public LuisRouterConfig GetConfiguration() => config;

        public LuisRouterAccessor BuildAccessor(UserState userState, IBotTelemetryClient botTelemetryClient = null)
        {
            Dictionary<string, LuisRecognizer> luisServices = BuildDictionary(botTelemetryClient);
            return new LuisRouterAccessor(userState, luisServices) { TokenPreference = userState.CreateProperty<string>("TokenPreference") };
        }

        public async Task GetTokenAsync(WaterfallStepContext step, LuisRouterAccessor accessor, string encryptedRequest)
        {
            using (var handler = new HttpClientHandler())
            {
                handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

                using (HttpClient client = new HttpClient(handler))
                {
                    byte[] byteData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { AppIdentity = encryptedRequest }));
                    using (var content = new ByteArrayContent(byteData))
                    {
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                        var response = await client.PostAsync($"{config.LuisRouterUrl}/identity", content);

                        if (response.IsSuccessStatusCode)
                        {
                            var json = await response.Content.ReadAsStringAsync();
                            var identityResponse = JsonConvert.DeserializeObject<IdentityResponse>(json);

                            await accessor.TokenPreference.SetAsync(step.Context, identityResponse.token);
                            await accessor.UserState.SaveChangesAsync(step.Context, false);
                        }
                    }
                }
            }
        }

        public async Task<List<LuisAppDetail>> LuisDiscoveryAsync(WaterfallStepContext step, LuisRouterAccessor accessor, string text, string applicationCode, string encryptionKey)
        {
            List<LuisAppDetail> result = new List<LuisAppDetail>();

            int IterationsToRetry = 3;
            int TimeToSleepForRetry = 100;

            for (int i = 0; i <= IterationsToRetry; i++)
            {
                using (var handler = new HttpClientHandler())
                {
                    handler.ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => true;

                    using (HttpClient client = new HttpClient(handler))
                    {
                        try
                        {
                            byte[] byteData = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { Text = text, BingSpellCheckSubscriptionKey = config.BingSpellCheckSubscriptionKey, EnableLuisTelemetry = config.EnableLuisTelemetry }));
                            using (var content = new ByteArrayContent(byteData))
                            {
                                string token = await accessor.TokenPreference.GetAsync(step.Context, () => { return string.Empty; });

                                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{token}");
                                var response = await client.PostAsync($"{config.LuisRouterUrl}/luisdiscovery", content);

                                if (response.IsSuccessStatusCode)
                                {
                                    var json = await response.Content.ReadAsStringAsync();
                                    var res = JsonConvert.DeserializeObject<LuisDiscoveryResponseResult>(json);
                                    result = res.Result.LuisAppDetails;
                                    break;
                                }
                                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                                {
                                    IdentityRequest request = new IdentityRequest()
                                    {
                                        appcode = applicationCode,
                                        timestamp = DateTime.UtcNow
                                    };

                                    string json = JsonConvert.SerializeObject(request);
                                    var encryptedRequest = NETCore.Encrypt.EncryptProvider.AESEncrypt(json, encryptionKey);
                                    await GetTokenAsync(step, accessor, encryptedRequest);
                                    continue;
                                }
                            }
                        }
                        catch
                        {
                            Thread.Sleep(TimeToSleepForRetry);
                            continue;
                        }
                    }
                }
            }

            return result;
        }

        private Dictionary<string, LuisRecognizer> BuildDictionary(IBotTelemetryClient botTelemetryClient = null)
        {
            Dictionary<string, LuisRecognizer> result = new Dictionary<string, LuisRecognizer>();

            foreach (LuisApp app in config.LuisApplications)
            {
                var luis = new LuisApplication(app.AppId, app.AuthoringKey, app.Endpoint);

                LuisPredictionOptions luisPredictionOptions = null;
                LuisRecognizer recognizer = null;

                bool needsPredictionOptions = false;
                if ((!string.IsNullOrEmpty(config.BingSpellCheckSubscriptionKey)) || (config.EnableLuisTelemetry))
                {
                    needsPredictionOptions = true;
                }

                if (needsPredictionOptions)
                {
                    luisPredictionOptions = new LuisPredictionOptions();

                    if (config.EnableLuisTelemetry)
                    {
                        luisPredictionOptions.TelemetryClient = botTelemetryClient;
                        luisPredictionOptions.Log = true;
                        luisPredictionOptions.LogPersonalInformation = true;
                    }

                    if (!string.IsNullOrEmpty(config.BingSpellCheckSubscriptionKey))
                    {
                        luisPredictionOptions.BingSpellCheckSubscriptionKey = config.BingSpellCheckSubscriptionKey;
                        luisPredictionOptions.SpellCheck = true;
                        luisPredictionOptions.IncludeAllIntents = true;
                    }

                    recognizer = new LuisRecognizer(luis, luisPredictionOptions);
                }
                else
                {
                    recognizer = new LuisRecognizer(luis);
                }

                result.Add(app.Name, recognizer);
            }

            return result;
        }
    }
}