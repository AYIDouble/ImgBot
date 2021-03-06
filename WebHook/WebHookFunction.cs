using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Common;
using Common.Messages;
using Common.TableModels;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage.Table;
using Newtonsoft.Json;
using WebHook.Model;

namespace WebHook
{
    public static class WebHookFunction
    {
        [FunctionName("WebHookFunction")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "hook")]HttpRequestMessage req,
            [Queue("routermessage")] ICollector<RouterMessage> routerMessages,
            [Queue("openprmessage")] ICollector<OpenPrMessage> openPrMessages,
            [Table("installation")] CloudTable installationTable,
            [Table("marketplace")] CloudTable marketplaceTable,
            ILogger logger)
        {
            var hookEvent = req.Headers.GetValues("X-GitHub-Event").First();
            var hook = JsonConvert.DeserializeObject<Hook>(await req.Content.ReadAsStringAsync());

            var result = "no action";

            switch (hookEvent)
            {
                case "installation_repositories":
                case "installation":
                case "integration_installation_repositories":
                case "integration_installation":
                    result = await ProcessInstallationAsync(hook, routerMessages, installationTable, logger);
                    break;
                case "push":
                    result = ProcessPush(hook, routerMessages, openPrMessages, logger);
                    break;
                case "marketplace_purchase":
                    result = await ProcessMarketplacePurchaseAsync(hook, marketplaceTable, logger);
                    break;
            }

            return new OkObjectResult(new HookResponse { Result = result });
        }

        private static string ProcessPush(Hook hook, ICollector<RouterMessage> routerMessages, ICollector<OpenPrMessage> openPrMessages, ILogger logger)
        {
            if (hook.@ref == $"refs/heads/{KnownGitHubs.BranchName}" && hook.sender.login == "imgbot[bot]")
            {
                openPrMessages.Add(new OpenPrMessage
                {
                    InstallationId = hook.installation.id,
                    RepoName = hook.repository.name,
                    CloneUrl = $"https://github.com/{hook.repository.full_name}",
                });

                logger.LogInformation("ProcessPush: Added OpenPrMessage for {Owner}/{RepoName}", hook.repository.owner, hook.repository.name);

                return "imgbot push";
            }

            if (hook.@ref != $"refs/heads/{hook.repository.default_branch}")
            {
                return "Commit to non default branch";
            }

            var files = hook.commits.SelectMany(x => x.added)
                .Concat(hook.commits.SelectMany(x => x.modified))
                .Where(file => KnownImgPatterns.ImgExtensions.Any(extension => file.EndsWith(extension, StringComparison.Ordinal)));

            if (files.Any() == false)
            {
                return "No image files touched";
            }

            routerMessages.Add(new RouterMessage
            {
                InstallationId = hook.installation.id,
                Owner = hook.repository.owner.login,
                RepoName = hook.repository.name,
                CloneUrl = $"https://github.com/{hook.repository.full_name}",
            });

            logger.LogInformation("ProcessPush: Added RouterMessage for {Owner}/{RepoName}", hook.repository.owner, hook.repository.name);

            return "truth";
        }

        private static async Task<string> ProcessInstallationAsync(Hook hook, ICollector<RouterMessage> routerMessages, CloudTable installationTable, ILogger logger)
        {
            switch (hook.action)
            {
                case "created":
                    foreach (var repo in hook.repositories)
                    {
                        routerMessages.Add(new RouterMessage
                        {
                            InstallationId = hook.installation.id,
                            Owner = hook.installation.account.login,
                            RepoName = repo.name,
                            CloneUrl = $"https://github.com/{repo.full_name}",
                        });

                        logger.LogInformation("ProcessInstallationAsync/created: Added RouterMessage for {Owner}/{RepoName}", repo.owner, repo.name);
                    }

                    break;
                case "added":
                    foreach (var repo in hook.repositories_added)
                    {
                        routerMessages.Add(new RouterMessage
                        {
                            InstallationId = hook.installation.id,
                            Owner = hook.installation.account.login,
                            RepoName = repo.name,
                            CloneUrl = $"https://github.com/{repo.full_name}",
                        });

                        logger.LogInformation("ProcessInstallationAsync/added: Added RouterMessage for {Owner}/{RepoName}", repo.owner, repo.name);
                    }

                    break;
                case "removed":
                    foreach (var repo in hook.repositories_removed)
                    {
                        await installationTable.DropRow(hook.installation.id.ToString(), repo.name);
                        logger.LogInformation("ProcessInstallationAsync/removed: DropRow for {InstallationId} :: {RepoName}", hook.installation.id, repo.name);
                    }

                    break;
                case "deleted":
                    await installationTable.DropPartitionAsync(hook.installation.id.ToString());
                    logger.LogInformation("ProcessInstallationAsync/deleted: DropPartition for {InstallationId}", hook.installation.id);
                    break;
            }

            return "truth";
        }

        private static async Task<string> ProcessMarketplacePurchaseAsync(Hook hook, CloudTable marketplaceTable, ILogger logger)
        {
            switch (hook.action)
            {
                case "purchased":
                    await marketplaceTable.ExecuteAsync(TableOperation.InsertOrMerge(new Marketplace(hook.marketplace_purchase.account.id, hook.marketplace_purchase.account.login)
                    {
                        AccountType = hook.marketplace_purchase.account.type,
                        PlanId = hook.marketplace_purchase.plan.id,
                        SenderId = hook.sender.id,
                        SenderLogin = hook.sender.login,
                    }));

                    logger.LogInformation("ProcessMarketplacePurchaseAsync/purchased for {Owner}", hook.marketplace_purchase.account.login);

                    return "purchased";
                case "cancelled":
                    await marketplaceTable.DropRow(hook.marketplace_purchase.account.id, hook.marketplace_purchase.account.login);
                    logger.LogInformation("ProcessMarketplacePurchaseAsync/cancelled for {Owner}", hook.marketplace_purchase.account.login);
                    return "cancelled";
                default:
                    return hook.action;
            }
        }
    }
}
