using CloudFoundry.CloudController.V2;
using CloudFoundry.CloudController.V2.Client;
using CloudFoundry.CloudController.V2.Client.Data;
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

namespace cfcmd
{


    [ArgExceptionBehavior(ArgExceptionPolicy.StandardExceptionHandling)]
    public class CloudFoundryCommand
    {
        private const string TOKEN_FILE = ".cf_token";
        private string api;
        private string token;
        private bool skipSSL;

        [HelpHook, ArgShortcut("-?"), ArgDescription("Shows this help")]
        public bool Help { get; set; }

        [ArgActionMethod, ArgDescription("List all applications")]
        public void Apps()
        {
            CloudFoundryClient client = SetTargetInfoFromFile();

            PagedResponseCollection<ListAllAppsResponse> apps = client.Apps.ListAllApps().Result;
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

        [ArgActionMethod, ArgDescription("List all stacks")]
        public void Stacks()
        {
            CloudFoundryClient client = SetTargetInfoFromFile();

            PagedResponseCollection<ListAllStacksResponse> stacks = client.Stacks.ListAllStacks().Result;
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
            List<Application> manifestApps = new List<Application>();
            List<Application> apps = new List<Application>();

            if(!pushArgs.NoManifest)
            {
                string path;
                if(!string.IsNullOrWhiteSpace(pushArgs.Manifest))
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
                catch{
                    if(!string.IsNullOrWhiteSpace(pushArgs.Manifest))
                    {
                        throw new FileNotFoundException("Error reading manifest file.");
                    }
                }
                
            }
            if(manifestApps.Count == 0)
            {
                apps.Add(new Application()
                {
                    Name = pushArgs.Name,
                    Memory = pushArgs.Memory,
                    Path = pushArgs.Dir,
                    StackName = pushArgs.Stack
                });
            }
            else if (manifestApps.Count == 1)
            {
                Application app = manifestApps[0];
                if(!string.IsNullOrWhiteSpace(pushArgs.Name))
                    app.Name = pushArgs.Name;
                if(pushArgs.Memory != 0)
                    app.Memory = pushArgs.Memory;
                if (!string.IsNullOrWhiteSpace(pushArgs.Stack))
                    app.StackName = pushArgs.Stack;
                if (!string.IsNullOrWhiteSpace(pushArgs.Dir))
                    app.Path = pushArgs.Dir;
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

            foreach (Application app in apps)
            {
                if (!Directory.Exists(app.Path))
                {
                    throw new DirectoryNotFoundException(string.Format("Directory '{0}' not found", app.Path));
                }

                CloudFoundryClient client = SetTargetInfoFromFile();

                // ======= GRAB FIRST SPACE AVAILABLE =======

                new ConsoleString("Looking up spaces ...", ConsoleColor.Cyan).WriteLine();

                PagedResponseCollection<ListAllSpacesResponse> spaces = client.Spaces.ListAllSpaces().Result;

                if (spaces.Count() == 0)
                {
                    throw new InvalidOperationException("Couldn't find any spaces");
                }

                ListAllSpacesResponse space = spaces.First();

                new ConsoleString(string.Format("Will be using space {0}", space.Name), ConsoleColor.Green).WriteLine();

                // ======= CREATE AN APPLICATION =======

                new ConsoleString("Creating app ...", ConsoleColor.Cyan).WriteLine();


                PagedResponseCollection<ListAllStacksResponse> stacks = client.Stacks.ListAllStacks(new RequestOptions()
                {
                    Query = string.Format("name:{0}", app.StackName)
                }).Result;

                if (stacks.Count() == 0)
                {
                    throw new InvalidOperationException(string.Format("Couldn't find the stack {0}", app.StackName));
                }

                CreateAppRequest createAppRequest = new CreateAppRequest()
                {
                    Name = app.Name,
                    Memory = (int)app.Memory,
                    StackGuid = new Guid(stacks.First().EntityMetadata.Guid),
                    SpaceGuid = new Guid(space.EntityMetadata.Guid),
                    Instances = app.InstanceCount,
                    Buildpack = app.BuildpackUrl,
                    Command = app.Command,
                    EnvironmentJson = app.EnvironmentVariables,
                    HealthCheckTimeout = app.HealthCheckTimeout
                };

                CreateAppResponse appCreateResponse = client.Apps.CreateApp(createAppRequest).Result;

                // ======= BIND SERVICES

                PagedResponseCollection<ListAllServiceInstancesResponse> allServices = client.ServiceInstances.ListAllServiceInstances().Result;

                foreach(string service in app.GetServices())
                {
                    new ConsoleString(string.Format("Binding service {0} to app {1} ...", service, app.Name), ConsoleColor.Cyan).WriteLine();

                    var currentService = allServices.FirstOrDefault(s => s.Name.ToUpperInvariant() == service.ToUpperInvariant());
                    if(currentService == null)
                    {
                        throw new InvalidOperationException(string.Format("Could not find service {0}", service));
                    }
                    CreateServiceBindingRequest request = new CreateServiceBindingRequest()
                    {
                        AppGuid = appCreateResponse.EntityMetadata.Guid,
                        ServiceInstanceGuid = currentService.EntityMetadata.Guid
                    };

                    client.ServiceBindings.CreateServiceBinding(request).Wait();
                }

                new ConsoleString(string.Format("Created app with guid '{0}'", appCreateResponse.EntityMetadata.Guid), ConsoleColor.Green).WriteLine();

                // ======= CREATE ROUTES =======

                PagedResponseCollection<ListAllSharedDomainsResponse> allDomains = client.SharedDomains.ListAllSharedDomains().Result;

                foreach (string domain in app.GetDomains())
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

                    foreach (string host in app.GetHosts())
                    {
                        string url = string.Format("{0}.{1}", host, domain);

                        CreateRouteResponse createRouteResponse = client.Routes.CreateRoute(new CreateRouteRequest()
                        {
                            DomainGuid = new Guid(currentDomain.EntityMetadata.Guid),
                            Host = host,
                            SpaceGuid = new Guid(space.EntityMetadata.Guid)
                        }).Result;

                        new ConsoleString(string.Format("Created route '{0}.{1}'", host, domain), ConsoleColor.Green).WriteLine();

                        // ======= BIND THE ROUTE =======

                        new ConsoleString("Associating the route ...", ConsoleColor.Cyan).WriteLine();

                        client.Routes.AssociateAppWithRoute(
                            new Guid(createRouteResponse.EntityMetadata.Guid),
                            new Guid(appCreateResponse.EntityMetadata.Guid)).Wait();
                    }
                }
                // ======= HOOKUP LOGGING =======
                // TODO: detect logyard vs loggregator

                GetV1InfoResponse v1Info = client.Info.GetV1Info().Result;
                LogyardLog logyard = new LogyardLog(new Uri(v1Info.AppLogEndpoint), string.Format("bearer {0}", client.AuthorizationToken));

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

                logyard.StartLogStream(appCreateResponse.EntityMetadata.Guid, 0, true);

                // ======= PUSH THE APP =======
                new ConsoleString("Pushing the app ...", ConsoleColor.Cyan).WriteLine();
                client.Apps.PushProgress += (sender, progress) =>
                {
                    new ConsoleString(string.Format("Push at {0}%", progress.Percent), ConsoleColor.Yellow).WriteLine();
                    new ConsoleString(string.Format("{0}", progress.Message), ConsoleColor.DarkYellow).WriteLine();
                };

                client.Apps.Push(new Guid(appCreateResponse.EntityMetadata.Guid), app.Path, true).Wait();

                // ======= WAIT FOR APP TO COME ONLINE =======
                while (true)
                {
                    GetAppSummaryResponse appSummary = client.Apps.GetAppSummary(new Guid(appCreateResponse.EntityMetadata.Guid)).Result;

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

                logyard.StopLogStream();

                new ConsoleString(string.Format("App is running, done."), ConsoleColor.Green).WriteLine();
            }
        }

        [ArgActionMethod, ArgDescription("Deletes an application")]
        public void DeleteApp(DeleteArgs deleteArgs)
        {
            CloudFoundryClient client = SetTargetInfoFromFile();

            PagedResponseCollection<ListAllAppsResponse> apps = client.Apps.ListAllApps(new RequestOptions()
            {
                Query = string.Format("name:{0}", deleteArgs.Name)
            }).Result;


            if (apps.Count() == 0)
            {
                throw new InvalidOperationException(string.Format("Couldn't find an app with name {0}", deleteArgs.Name));
            }

            client.Apps.DeleteApp(new Guid(apps.First().EntityMetadata.Guid)).Wait();

            new ConsoleString(string.Format("Deleted app with id {0}", apps.First().EntityMetadata.Guid), ConsoleColor.Green).WriteLine();
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
