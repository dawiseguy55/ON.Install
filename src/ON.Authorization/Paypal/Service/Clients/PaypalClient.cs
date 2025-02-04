﻿using Microsoft.Extensions.Options;
using ON.Authorization.Paypal.Service.Clients.Models;
using ON.Authorization.Paypal.Service.Data;
using ON.Authorization.Paypal.Service.Models;
using ON.Fragments.Authorization.Payments.Paypal;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ON.Authorization.Paypal.Service.Clients
{
    public class PaypalClient
    {
        public readonly PlanList Plans;

        private readonly AppSettings settings;
        private readonly IPlanRecordProvider recordProvider;

        private Task loginTask;
        private string bearerToken;
        private DateTime bearerExpiration = DateTime.MinValue;
        private DateTime bearerSoftExpiration = DateTime.MinValue;

        private object syncObject = new();

        public PaypalClient(IOptions<AppSettings> settings, IPlanRecordProvider recordProvider)
        {
            this.settings = settings.Value;
            this.recordProvider = recordProvider;

            Plans = recordProvider.GetAll().Result;

            EnsurePlans().Wait();
        }

        private async Task EnsurePlans()
        {
            foreach (var tier in SiteConfig.SubscriptionTiers)
                if (tier.Value > 0)
                    await EnsurePlan(tier);
        }

        private async Task EnsurePlan(SubscriptionTier tier)
        {
            var p = Plans.Records.FirstOrDefault(x => x.Value == tier.Value);
            if (p != null)
            {
                var pm = await GetPlan(p.PlanId);
                if (pm.status == "ACTIVE"
                    && pm?.billing_cycles?.FirstOrDefault()?.pricing_scheme?.fixed_price?.value == tier.Value.ToString("0.0")
                    && pm?.name == tier.Name)
                {
                    return;
                }
                Plans.Records.Remove(p);
            }

            var created = await CreatePlan(tier);
            if (created != null)
            {
                Plans.Records.Add(new PlanRecord()
                {
                    Value = (uint)tier.Value,
                    Name = tier.Name,
                    PlanId = created.id,
                });
                await recordProvider.SaveAll(Plans);
            }
        }

        private async Task EnsureProduct(SubscriptionTier tier)
        {
            var p = await GetProduct(tier);
            if (p != null)
                return;

            await CreateProduct(tier);
        }

        private async Task<PlanRecordModel> CreatePlan(SubscriptionTier tier)
        {
            try
            {
                await EnsureProduct(tier);

                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var plan = PlanRecordModel.Create(tier, GetProductId(tier));
                plan.product_id = GetProductId(tier);
                plan.name = tier.Name;

                var httpRes = await client.PostAsJsonAsync("/v1/billing/plans", plan, timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<PlanRecordModel>(await httpRes.Content.ReadAsStringAsync());
            }
            catch { }

            return null;
        }

        internal async Task<bool> CancelSubscription(string subscriptionId, string reason)
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var httpRes = await client.PostAsJsonAsync("/v1/billing/subscriptions/" + subscriptionId + "/cancel", new { reason = reason }, timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return true;
            }
            catch { }

            return false;
        }

        internal async Task<SubscriptionModel> GetSubscription(string subscriptionId)
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var httpRes = await client.GetAsync("/v1/billing/subscriptions/" + subscriptionId, timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<SubscriptionModel>(await httpRes.Content.ReadAsStringAsync());
            }
            catch { }

            return null;
        }

        private async Task<ProductRecordModel> CreateProduct(SubscriptionTier tier)
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var product = new ProductRecordModel()
                {
                    id = GetProductId(tier),
                    name = tier.Name,
                };

                var httpRes = await client.PostAsJsonAsync("/v1/catalogs/products", product, timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<ProductRecordModel>(await httpRes.Content.ReadAsStringAsync());
            }
            catch { }

            return null;
        }

        private async Task<PlanRecordModel> GetPlan(string planId)
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var httpRes = await client.GetAsync("/v1/billing/plans/" + planId, timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<PlanRecordModel>(await httpRes.Content.ReadAsStringAsync());
            }
            catch { }

            return null;
        }

        private async Task<ProductRecordModel> GetProduct(SubscriptionTier tier)
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                var client = await GetClient();

                var httpRes = await client.GetAsync("/v1/catalogs/products/" + GetProductId(tier), timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<ProductRecordModel>(await httpRes.Content.ReadAsStringAsync());
            }
            catch { }

            return null;
        }

        private string GetProductId(SubscriptionTier tier)
        {
            return "ONF-PROD-" + tier.Value;
        }

        private async Task<HttpClient> GetClient()
        {
            var token = await GetBearerToken();
            if (token == null)
                return null;

            HttpClient client = new HttpClient();
            client.BaseAddress = new Uri(settings.PaypalUrl);
            client.DefaultRequestHeaders.Clear();
            client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json"));
            client.DefaultRequestHeaders.AcceptLanguage.Add(StringWithQualityHeaderValue.Parse("en_US"));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return client;
        }

        private async Task<string> GetBearerToken()
        {
            var now = DateTime.UtcNow;

            if (now > bearerSoftExpiration)
            {
                lock (syncObject)
                {
                    if (loginTask == null)
                    {
                        loginTask = DoLogin();
                    }
                }
            }

            if (now > bearerExpiration)
                await loginTask;

            return bearerToken;
        }

        private async Task DoLogin()
        {
            try
            {
                CancellationTokenSource timeout = new CancellationTokenSource();
                timeout.CancelAfter(3000);
                using HttpClient client = new HttpClient();
                client.BaseAddress = new Uri(settings.PaypalUrl);
                client.DefaultRequestHeaders.Clear();
                client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json"));
                client.DefaultRequestHeaders.AcceptLanguage.Add(StringWithQualityHeaderValue.Parse("en_US"));
                client.DefaultRequestHeaders.ConnectionClose = true;

                var authenticationString = settings.PaypalClientID + ":" + settings.PaypalClientSecret;
                var base64EncodedAuthenticationString = Convert.ToBase64String(Encoding.ASCII.GetBytes(authenticationString));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", base64EncodedAuthenticationString);

                var dict = new Dictionary<string, string>();
                dict["grant_type"] = "client_credentials";

                var httpRes = await client.PostAsync("/v1/oauth2/token", new FormUrlEncodedContent(dict), timeout.Token);

                if (httpRes.IsSuccessStatusCode)
                {
                    var jsonRes = JsonSerializer.Deserialize<OAuthResponseModel>(await httpRes.Content.ReadAsStringAsync());
                    if (jsonRes != null)
                    {
                        bearerToken = jsonRes.access_token;
                        bearerExpiration = DateTime.UtcNow.AddSeconds(jsonRes.expires_in);
                        bearerSoftExpiration = DateTime.UtcNow.AddSeconds(jsonRes.expires_in / 2);
                    }
                }
            }
            catch
            {

            }
            loginTask = null;
        }
    }
}
