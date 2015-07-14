using CloudFoundry.CloudController.V3;
using CloudFoundry.CloudController.V3.Client;
using CloudFoundry.CloudController.V3.Client.Data;
using CCV2 = CloudFoundry.CloudController.V2.Client;
using CloudFoundry.Logyard.Client;
using CloudFoundry.Manifests;
using CloudFoundry.Manifests.Models;
using CloudFoundry.UAA;
using PowerArgs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Collections;

namespace cfcmd
{


    [ArgExceptionBehavior(ArgExceptionPolicy.StandardExceptionHandling)]
    public class CloudFoundryCommand
    {
        private const string TOKEN_FILE = ".cf_token";
        private string api;
        private string token;
        private bool skipSSL;
        private bool useV3 = false;

        [HelpHook, ArgShortcut("-?"), ArgDescription("Shows this help")]
        public bool Help { get; set; }

        [ArgActionMethod, ArgDescription("List all applications")]
        public void Apps(AppsArgs appsArgs)
        {
            CloudFoundryClient client = SetTargetInfoFromFile();
            if (appsArgs.V3)
            {
                var apps = client.Apps.ListAllApps().Result;
                while (apps != null && apps.Pagination.TotalResults != 0)
                {
                    foreach (var app in apps)
                    {
                        new ConsoleString(string.Format("{0}", app.Name), ConsoleColor.Green).WriteLine();
                        new ConsoleString("------------------", ConsoleColor.Gray).WriteLine();
                    }

                    apps = apps.GetNextPage().Result;
                }
            }
            else
            {
                var apps = client.V2.Apps.ListAllApps().Result;
                while (apps != null && apps.Properties.TotalResults != 0)
                {
                    foreach (var app in apps)
                    {
                        new ConsoleString(string.Format("{0}", app.Name), ConsoleColor.Green).WriteLine();
                        new ConsoleString(string.Format("Memory: {0}", app.Memory), ConsoleColor.White).WriteLine();
                        new ConsoleString(string.Format("Buildpack: {0}", app.Buildpack ?? app.DetectedBuildpack ?? "n/a"), ConsoleColor.White).WriteLine();
                        new ConsoleString(string.Format("Instances: {0}", app.Instances), ConsoleColor.White).WriteLine();
                        new ConsoleString("------------------", ConsoleColor.Gray).WriteLine();
                    }

                    apps = apps.GetNextPage().Result;
                }
            }
        }

        [ArgActionMethod, ArgDescription("List all stacks")]
        public void Stacks()
        {
            CloudFoundryClient client = SetTargetInfoFromFile();

            var stacks = client.V2.Stacks.ListAllStacks().Result;
            while (stacks != null && stacks.Properties.TotalResults != 0)
            {
                foreach (var stack in stacks)
                {
                    new ConsoleString(string.Format("{0} - {1}", stack.Name, stack.Description), ConsoleColor.White).WriteLine();
                }

                stacks = stacks.GetNextPage().Result;
            }
        }

        [ArgActionMethod, ArgDescription("Push the current directory to the cloud")]
        public void Push(PushArgs pushArgs)
        {
            this.useV3 = pushArgs.V3;

            List<Application> manifestApps = new List<Application>();
            List<Application> apps = new List<Application>();

            if (!pushArgs.NoManifest)
            {
                string path;
                if (!string.IsNullOrWhiteSpace(pushArgs.Manifest))
                {
                    path = pushArgs.Manifest;
                }
                else
                {
                    path = Directory.GetCurrentDirectory();
                }
                try
                {
                    Manifest manifest = ManifestDiskRepository.ReadManifest(path);
                    manifestApps.AddRange(manifest.Applications().ToList());
                }
                catch
                {
                    if (!string.IsNullOrWhiteSpace(pushArgs.Manifest))
                    {
                        throw new FileNotFoundException("Error reading manifest file.");
                    }
                }

            }
            if (manifestApps.Count == 0)
            {
                var app = new Application()
                {
                    Name = pushArgs.Name,
                    Memory = pushArgs.Memory,
                    Path = pushArgs.Dir,
                    StackName = pushArgs.Stack
                };
                app.Hosts.Add(pushArgs.Name);
                apps.Add(app);
            }
            else if (manifestApps.Count == 1)
            {
                Application app = manifestApps[0];
                if (!string.IsNullOrWhiteSpace(pushArgs.Name))
                    app.Name = pushArgs.Name;
                if (pushArgs.Memory != 0)
                    app.Memory = pushArgs.Memory;
                if (!string.IsNullOrWhiteSpace(pushArgs.Stack))
                    app.StackName = pushArgs.Stack;
                if (!string.IsNullOrWhiteSpace(pushArgs.Dir))
                    app.Path = pushArgs.Dir;
                if(!app.NoHostName && app.Hosts.Count == 0)
                    app.Hosts.Add(pushArgs.Name);
                apps.Add(app);
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(pushArgs.Name))
                {
                    Application app = manifestApps.FirstOrDefault(a => a.Name == pushArgs.Name);
                    if (app == null)
                    {
                        throw new ArgumentException(string.Format("Could not find app named '{0}' in manifest", pushArgs.Name));
                    }
                    if (!string.IsNullOrWhiteSpace(pushArgs.Name))
                        app.Name = pushArgs.Name;
                    if (pushArgs.Memory != 0)
                        app.Memory = pushArgs.Memory;
                    if (!string.IsNullOrWhiteSpace(pushArgs.Stack))
                        app.StackName = pushArgs.Stack;
                    if (!string.IsNullOrWhiteSpace(pushArgs.Dir))
                        app.Path = pushArgs.Dir;
                    apps.Add(app);
                }
                else
                {
                    apps.AddRange(manifestApps);
                }
            }

            CloudFoundryClient client = SetTargetInfoFromFile();

            foreach (Application app in apps)
            {
                if (!Directory.Exists(app.Path))
                {
                    throw new DirectoryNotFoundException(string.Format("Directory '{0}' not found", app.Path));
                }

                // ======= GRAB FIRST SPACE AVAILABLE =======

                new ConsoleString("Looking up spaces ...", ConsoleColor.Cyan).WriteLine();

                var spaces = client.V2.Spaces.ListAllSpaces().Result;

                if (spaces.Count() == 0)
                {
                    throw new InvalidOperationException("Couldn't find any spaces");
                }

                CCV2.Data.ListAllSpacesResponse space = spaces.First();

                new ConsoleString(string.Format("Will be using space {0}", space.Name), ConsoleColor.Green).WriteLine();

                // ======= CREATE AN APPLICATION =======

                new ConsoleString("Creating app ...", ConsoleColor.Cyan).WriteLine();

                var stacks = client.V2.Stacks.ListAllStacks(new CCV2.RequestOptions()
                {
                    Query = string.Format("name:{0}", app.StackName)
                }).Result;

                if (stacks.Count() == 0)
                {
                    throw new InvalidOperationException(string.Format("Couldn't find the stack {0}", app.StackName));
                }

                Guid appGuid = Guid.Empty;

                if (pushArgs.V3)
                {
                    CreateAppRequest createAppRequest = new CreateAppRequest()
                    {
                        Name = app.Name,
                        SpaceGuid = new Guid(space.EntityMetadata.Guid)
                    };

                    CreateAppResponse appCreateResponse = client.Apps.CreateApp(createAppRequest).Result;
                    appGuid = new Guid(appCreateResponse.Guid.ToString());
                    new ConsoleString(string.Format("Created app with guid '{0}'", appCreateResponse.Guid), ConsoleColor.Green).WriteLine();
                }
                else
                {
                    CCV2.Data.CreateAppRequest createAppRequest = new CCV2.Data.CreateAppRequest()
                    {
                        Name = app.Name,
                        Memory = (int)app.Memory,
                        StackGuid = new Guid(stacks.First().EntityMetadata.Guid),
                        SpaceGuid = new Guid(space.EntityMetadata.Guid)
                    };

                    CCV2.Data.CreateAppResponse appCreateResponse = client.V2.Apps.CreateApp(createAppRequest).Result;
                    appGuid = appCreateResponse.EntityMetadata.Guid;
                    new ConsoleString(string.Format("Created app with guid '{0}'", appCreateResponse.EntityMetadata.Guid), ConsoleColor.Green).WriteLine();
                }

                // ======= PUSH THE APP =======
                new ConsoleString("Pushing the app ...", ConsoleColor.Cyan).WriteLine();

                if (pushArgs.V3)
                {
                    client.Apps.PushProgress += (sender, progress) =>
                    {
                        new ConsoleString(string.Format("Push at {0}%", progress.Percent), ConsoleColor.Yellow).WriteLine();
                        new ConsoleString(string.Format("{0}", progress.Message), ConsoleColor.DarkYellow).WriteLine();
                    };

                    client.Apps.Push(appGuid, app.Path, app.StackName, null, true).Wait();
                }
                else
                {
                    client.V2.Apps.PushProgress += (sender, progress) =>
                    {
                        new ConsoleString(string.Format("Push at {0}%", progress.Percent), ConsoleColor.Yellow).WriteLine();
                        new ConsoleString(string.Format("{0}", progress.Message), ConsoleColor.DarkYellow).WriteLine();
                    };

                    client.V2.Apps.Push(appGuid, app.Path, true).Wait();
                }

                // ======= HOOKUP LOGGING AND MONITOR APP =======

                Guid appGuidV2 = Guid.Empty;

                if (this.useV3)
                {
                    appGuidV2 = new Guid(client.Apps.ListAssociatedProcesses(appGuid).Result.First().Guid.ToString());
                }

                CCV2.Data.GetV1InfoResponse v1Info = client.V2.Info.GetV1Info().Result;

                if (string.IsNullOrWhiteSpace(v1Info.AppLogEndpoint) == false)
                {
                    this.GetLogsUsingLogyard(client, appGuidV2, v1Info);
                }
                else
                {
                    CCV2.Data.GetInfoResponse detailedInfo = client.V2.Info.GetInfo().Result;

                    if (string.IsNullOrWhiteSpace(detailedInfo.LoggingEndpoint) == false)
                    {
                        this.GetLogsUsingLoggregator(client, appGuidV2, detailedInfo);
                    }
                    else
                    {
                        throw new Exception("Could not retrieve application logs");
                    }
                }

                // ======= BIND SERVICES

                var allServices = client.V2.ServiceInstances.ListAllServiceInstances().Result;

                foreach (string service in app.Services)
                {
                    new ConsoleString(string.Format("Binding service {0} to app {1} ...", service, app.Name), ConsoleColor.Cyan).WriteLine();

                    var currentService = allServices.FirstOrDefault(s => s.Name.ToUpperInvariant() == service.ToUpperInvariant());
                    if (currentService == null)
                    {
                        throw new InvalidOperationException(string.Format("Could not find service {0}", service));
                    }
                    CCV2.Data.CreateServiceBindingRequest request = new CCV2.Data.CreateServiceBindingRequest()
                    {
                        AppGuid = appGuidV2,
                        ServiceInstanceGuid = currentService.EntityMetadata.Guid
                    };

                    client.V2.ServiceBindings.CreateServiceBinding(request).Wait();
                }

                // ======= CREATE ROUTES =======

                var allDomains = client.V2.SharedDomains.ListAllSharedDomains().Result;

                ArrayList domains = app.Domains;
                if(domains.Count == 0)
                {
                    domains.AddRange(allDomains.Select(d => d.Name).ToList());
                }

                foreach (string domain in domains)
                {
                    new ConsoleString("Creating a route ...", ConsoleColor.Cyan).WriteLine();

                    if (allDomains.Count() == 0)
                    {
                        throw new InvalidOperationException("Could not find any shared domains");
                    }

                    var currentDomain = allDomains.FirstOrDefault(d => d.Name.ToUpperInvariant() == domain.ToUpperInvariant());
                    if (currentDomain == null)
                    {
                        throw new InvalidOperationException(string.Format("Could not find domain {0}", domain));
                    }

                    foreach (string host in app.Hosts)
                    {
                        string url = string.Format("{0}.{1}", host, domain);

                        CCV2.Data.CreateRouteResponse createRouteResponse = client.V2.Routes.CreateRoute(new CCV2.Data.CreateRouteRequest()
                        {
                            DomainGuid = new Guid(currentDomain.EntityMetadata.Guid),
                            Host = host,
                            SpaceGuid = new Guid(space.EntityMetadata.Guid)
                        }).Result;

                        new ConsoleString(string.Format("Created route '{0}.{1}'", host, domain), ConsoleColor.Green).WriteLine();

                        // ======= BIND THE ROUTE =======

                        new ConsoleString("Associating the route ...", ConsoleColor.Cyan).WriteLine();

                        client.V2.Routes.AssociateAppWithRoute(
                            new Guid(createRouteResponse.EntityMetadata.Guid),
                            appGuidV2).Wait();
                    }
                }

                client.Apps.StoppingApp(appGuid, new StoppingAppRequest() { }).Wait();
                client.Apps.StartingApp(appGuid, new StartingAppRequest() { }).Wait();

                new ConsoleString(string.Format("App is running, done."), ConsoleColor.Green).WriteLine();
            }
        }

        [ArgActionMethod, ArgDescription("Deletes an application")]
        public void DeleteApp(DeleteArgs deleteArgs)
        {
            CloudFoundryClient client = SetTargetInfoFromFile();

            if (!deleteArgs.V3)
            {
                CCV2.PagedResponseCollection<CCV2.Data.ListAllAppsResponse> apps = client.V2.Apps.ListAllApps(new CCV2.RequestOptions()
                {
                    Query = string.Format("name:{0}", deleteArgs.Name)
                }).Result;


                if (apps.Count() == 0)
                {
                    throw new InvalidOperationException(string.Format("Couldn't find an app with name {0}", deleteArgs.Name));
                }

                client.V2.Apps.DeleteApp(new Guid(apps.First().EntityMetadata.Guid)).Wait();
                new ConsoleString(string.Format("Deleted app with id {0}", apps.First().EntityMetadata.Guid), ConsoleColor.Green).WriteLine();
            }
            else
            {
                RequestOptions query = new RequestOptions();
                query.Query.Add("names", new string[] { deleteArgs.Name });

                PagedResponseCollection<ListAllAppsResponse> apps = client.Apps.ListAllApps(query).Result;

                if (apps.Count() == 0)
                {
                    throw new InvalidOperationException(string.Format("Couldn't find an app with name {0}", deleteArgs.Name));
                }

                client.Apps.DeleteApp(new Guid(apps.First().Guid)).Wait();
                new ConsoleString(string.Format("Deleted app with id {0}", apps.First().Guid), ConsoleColor.Green).WriteLine();
            }
        }

        [ArgActionMethod, ArgDescription("Login to a CloudFoundry target and save a .cf_token file in the current directory")]
        public void Login(LoginArgs loginArgs)
        {
            if (loginArgs.SkipSSL)
            {
                new ConsoleString("Ignoring SSL errors.", ConsoleColor.Yellow).WriteLine();
                System.Net.ServicePointManager.ServerCertificateValidationCallback = ((sender, certificate, chain, sslPolicyErrors) => true);
            }

            CloudCredentials credentials = new CloudCredentials()
            {
                User = loginArgs.User,
                Password = loginArgs.Password.ConvertToNonsecureString()
            };

            Console.WriteLine();

            if (!loginArgs.Api.ToLower().StartsWith("http") && !loginArgs.Api.ToLower().StartsWith("https"))
            {
                loginArgs.Api = "https://" + loginArgs.Api;
            }

            new ConsoleString(string.Format("Connecting to {0} ...", loginArgs.Api), ConsoleColor.DarkCyan).WriteLine();


            CloudFoundryClient client = new CloudFoundryClient(new Uri(loginArgs.Api), new System.Threading.CancellationToken());

            SaveTokenFile(
                loginArgs.Api,
                client.Login(credentials).Result,
                loginArgs.SkipSSL);
        }

        private static void SaveTokenFile(string api, AuthenticationContext authContext, bool skipSSL)
        {
            string[] tokenFile = new string[] {
                api.ToString(),
                authContext.Token.RefreshToken,
                skipSSL.ToString()
            };

            File.WriteAllLines(TOKEN_FILE, tokenFile);
        }

        private CloudFoundryClient SetTargetInfoFromFile()
        {
            if (!File.Exists(TOKEN_FILE))
            {
                throw new Exception("Token file not found. Please login first.");
            }

            string[] tokenFileInfo = File.ReadAllLines(TOKEN_FILE);

            this.api = tokenFileInfo[0];
            this.token = tokenFileInfo[1];
            this.skipSSL = Convert.ToBoolean(tokenFileInfo[2]);

            if (this.skipSSL)
            {
                new ConsoleString("Ignoring SSL errors.", ConsoleColor.Yellow).WriteLine();
                System.Net.ServicePointManager.ServerCertificateValidationCallback = ((sender, certificate, chain, sslPolicyErrors) => true);
            }

            new ConsoleString(string.Format("Logging in to target {0} ...", this.api), ConsoleColor.DarkCyan).WriteLine();

            CloudFoundryClient client = new CloudFoundryClient(new Uri(this.api), new System.Threading.CancellationToken());
            client.Login(this.token).Wait();

            new ConsoleString(string.Format("Logged in.", this.api), ConsoleColor.Green).WriteLine();

            return client;
        }

        private void GetLogsUsingLoggregator(CloudFoundryClient client, Guid? appGuid, CCV2.Data.GetInfoResponse detailedInfo)
        {
            using (CloudFoundry.Loggregator.Client.LoggregatorLog loggregator = new CloudFoundry.Loggregator.Client.LoggregatorLog(new Uri(detailedInfo.LoggingEndpoint), string.Format(CultureInfo.InvariantCulture, "bearer {0}", client.AuthorizationToken), null, this.skipSSL))
            {
                loggregator.ErrorReceived += (sender, error) =>
                {
                    Program.PrintExceptionMessage(error.Error);
                };

                loggregator.StreamOpened += (sender, args) =>
                {
                    new ConsoleString("Log stream opened.", ConsoleColor.Cyan).WriteLine();
                };

                loggregator.StreamClosed += (sender, args) =>
                {
                    new ConsoleString("Log stream closed.", ConsoleColor.Cyan).WriteLine();
                };

                loggregator.MessageReceived += (sender, message) =>
                {
                    var timeStamp = message.LogMessage.Timestamp / 1000 / 1000;
                    var time = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(timeStamp);

                    new ConsoleString(
                   string.Format("[{0}] - {1}: {2}",
                   message.LogMessage.SourceName,
                   time.ToString(),
                   message.LogMessage.Message), ConsoleColor.White).WriteLine();
                };

                loggregator.Tail(appGuid.Value.ToString());

                this.MonitorApp(client, appGuid);

                loggregator.StopLogStream();
            }
        }

        private void GetLogsUsingLogyard(CloudFoundryClient client, Guid? appGuid, CCV2.Data.GetV1InfoResponse info)
        {
            using (LogyardLog logyard = new LogyardLog(new Uri(info.AppLogEndpoint), string.Format(CultureInfo.InvariantCulture, "bearer {0}", client.AuthorizationToken), null, this.skipSSL))
            {
                logyard.ErrorReceived += (sender, error) =>
                {
                    Program.PrintExceptionMessage(error.Error);
                };

                logyard.StreamOpened += (sender, args) =>
                {
                    new ConsoleString("Log stream opened.", ConsoleColor.Cyan).WriteLine();
                };

                logyard.StreamClosed += (sender, args) =>
                {
                    new ConsoleString("Log stream closed.", ConsoleColor.Cyan).WriteLine();
                };

                logyard.MessageReceived += (sender, message) =>
                {
                    new ConsoleString(
                        string.Format("[{0}] - {1}: {2}",
                        message.Message.Value.Source,
                        message.Message.Value.HumanTime,
                        message.Message.Value.Text),
                        ConsoleColor.White).WriteLine();
                };

                logyard.StartLogStream(appGuid.Value.ToString(), 0, true);

                this.MonitorApp(client, appGuid);

                logyard.StopLogStream();
            }
        }

        private void MonitorApp(CloudFoundryClient client, Guid? appGuid)
        {
            // ======= WAIT FOR APP TO COME ONLINE =======
            while (true)
            {
                CCV2.Data.GetAppSummaryResponse appSummary = client.V2.Apps.GetAppSummary(appGuid).Result;

                if (appSummary.RunningInstances > 0)
                {
                    break;
                }

                if (appSummary.PackageState == "FAILED")
                {
                    throw new Exception("App staging failed.");
                }
                else if (appSummary.PackageState == "PENDING")
                {
                    new ConsoleString("[cfcmd] - App is staging ...", ConsoleColor.DarkCyan).WriteLine();
                }
                else if (appSummary.PackageState == "STAGED")
                {
                    new ConsoleString("[cfcmd] - App staged, waiting for it to come online ...", ConsoleColor.DarkCyan).WriteLine();
                }
                Thread.Sleep(2000);
            }
        }
    }

    public class LoginArgs
    {
        [ArgShortcut("--api"), ArgShortcut("-a"), ArgDescription("Target API"), ArgRequired(PromptIfMissing = true)]
        public string Api { get; set; }

        [ArgShortcut("--user"), ArgShortcut("-u"), ArgDescription("CloudFoundry Username"), ArgRequired(PromptIfMissing = true)]
        public string User { get; set; }

        [ArgShortcut("--pass"), ArgShortcut("-p"), ArgDescription("CloudFoundry Password")]
        public SecureStringArgument Password { get; set; }

        [ArgShortcut("--skip-ssl"), ArgShortcut("-x"), ArgDescription("Skip ssl validation")]
        public bool SkipSSL { get; set; }
    }

    public class DeleteArgs
    {
        [ArgShortcut("--name"), ArgShortcut("-n"), ArgDescription("Name of app"), ArgRequired(PromptIfMissing = true)]
        public string Name { get; set; }

        [ArgShortcut("--v3"), ArgDescription("Use the v3 api")]
        public bool V3 { get; set; }
    }

    public class PushArgs
    {
        [ArgShortcut("--name"), ArgShortcut("-n"), ArgDescription("Name of app"), ArgRequired(IfNot = "Manifest")]
        public string Name { get; set; }

        [ArgShortcut("--memory"), ArgShortcut("-m"), ArgDescription("Amount of memory to allocate"), ArgRequired(IfNot = "Manifest")]
        public int Memory { get; set; }

        [ArgShortcut("--stack"), ArgShortcut("-s"), ArgDescription("Stack to use"), ArgRequired(IfNot = "Manifest")]
        public string Stack { get; set; }

        [ArgShortcut("--dir"), ArgShortcut("-d"), ArgDescription("Directory you want to push"), ArgRequired(IfNot = "Manifest")]
        public string Dir { get; set; }


        [ArgShortcut("--manifest"), ArgShortcut("-f"), ArgDescription("Path to manifest")]
        public string Manifest { get; set; }

        [ArgShortcut("--no-manifest"), ArgDescription("Ignore manifests if they exist.")]
        public bool NoManifest { get; set; }

        [ArgShortcut("--v3"), ArgDescription("Use the v3 api")]
        public bool V3 { get; set; }
    }

    public class AppsArgs
    {
        [ArgShortcut("--v3"), ArgDescription("Use the v3 api")]
        public bool V3 { get; set; }
    }

    class Program
    {
        internal static void PrintExceptionMessage(Exception ex)
        {
            if (ex is AggregateException)
            {
                foreach (Exception iex in (ex as AggregateException).Flatten().InnerExceptions)
                {
                    PrintExceptionMessage(iex);
                }
            }
            else
            {
                new ConsoleString(ex.Message, ConsoleColor.Red).WriteLine();
                if (ex.InnerException != null)
                {
                    PrintExceptionMessage(ex.InnerException);
                }
            }
        }

        static void Main(string[] args)
        {
            try
            {
                Args.InvokeAction<CloudFoundryCommand>(args);
            }
            catch (Exception ex)
            {
                PrintExceptionMessage(ex);
            }
        }
    }
}
