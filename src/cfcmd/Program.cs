using CloudFoundry.CloudController.V2;
using CloudFoundry.CloudController.V2.Client.Data;
using CloudFoundry.Logyard.Client;
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
            if (!Directory.Exists(pushArgs.Dir))
            {
                throw new DirectoryNotFoundException(string.Format("Directory '{0}' not found", pushArgs.Dir));
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
                Query = string.Format("name:{0}", pushArgs.Stack)
            }).Result;

            if (stacks.Count() == 0)
            {
                throw new InvalidOperationException(string.Format("Couldn't find the stack {0}", pushArgs.Stack));
            }

            CreateAppRequest createAppRequest = new CreateAppRequest()
            {
                Name = pushArgs.Name,
                Memory = pushArgs.Memory,
                StackGuid = new Guid(stacks.First().EntityMetadata.Guid),
                SpaceGuid = new Guid(space.EntityMetadata.Guid)
            };

            CreateAppResponse appCreateResponse = client.Apps.CreateApp(createAppRequest).Result;

            new ConsoleString(string.Format("Created app with guid '{0}'", appCreateResponse.EntityMetadata.Guid), ConsoleColor.Green).WriteLine();

            // ======= CREATE A ROUTE =======

            new ConsoleString("Creating a route ...", ConsoleColor.Cyan).WriteLine();

            PagedResponseCollection<ListAllSharedDomainsResponse> allDomains = client.SharedDomains.ListAllSharedDomains().Result;

            if (allDomains.Count() == 0)
            {
                throw new InvalidOperationException("Could not find any shared domains");
            }

            string url = string.Format("{0}.{1}", pushArgs.Name, allDomains.First().Name);

            CreateRouteResponse createRouteResponse = client.Routes.CreateRoute(new CreateRouteRequest()
            {
                DomainGuid = new Guid(allDomains.First().EntityMetadata.Guid),
                Host = pushArgs.Name,
                SpaceGuid = new Guid(space.EntityMetadata.Guid)
            }).Result;

            new ConsoleString(string.Format("Created route '{0}.{1}'", pushArgs.Name, allDomains.First().Name), ConsoleColor.Green).WriteLine();

            // ======= BIND THE ROUTE =======

            new ConsoleString("Associating the route ...", ConsoleColor.Cyan).WriteLine();

            client.Routes.AssociateAppWithRoute(
                new Guid(createRouteResponse.EntityMetadata.Guid),
                new Guid(appCreateResponse.EntityMetadata.Guid)).Wait();

            // ======= HOOKUP LOGGING =======
            // TODO: detect logyard vs loggregator

            GetV1InfoResponse v1Info = client.Info.GetV1Info().Result;
            LogyardLog logyard = new LogyardLog(new Uri(v1Info.AppLogEndpoint), client.AuthorizationToken);

            logyard.ErrorReceived += (sender, error) =>
            {
                Program.PrintExceptionMessage(error.Error);
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


            // ======= PUSH THE APP =======
            new ConsoleString("Pushing the app ...", ConsoleColor.Cyan).WriteLine();
            client.Apps.PushProgress += (sender, progress) =>
            {
                new ConsoleString(string.Format("Push at {0}%", progress.Percent), ConsoleColor.Yellow).WriteLine();
                new ConsoleString(string.Format("{0}", progress.Message), ConsoleColor.DarkYellow).WriteLine();
            };

            client.Apps.Push(new Guid(appCreateResponse.EntityMetadata.Guid), pushArgs.Dir, true).Wait();

            // ======= WAIT FOR APP TO COME ONLINE =======
            bool done = false;
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
                    new ConsoleString("[cfcmd] - App is staging ...", ConsoleColor.Magenta).WriteLine();
                }
                else if (appSummary.PackageState == "STAGED")
                {
                    new ConsoleString("[cfcmd] - App staged, waiting for it to come online ...", ConsoleColor.Magenta).WriteLine();
                }

                Thread.Sleep(2000);
            }


            new ConsoleString(string.Format("App is running, done. You can browse it here: http://{0}.{1}", pushArgs.Name, allDomains.First().Name) , ConsoleColor.Green).WriteLine();
        }

        [ArgActionMethod, ArgDescription("Deletes an application")]
        public void DeleteApp(string app, string space)
        {
            SetTargetInfoFromFile();
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

            new ConsoleString(string.Format("Connecting to {0} ...", loginArgs.Api), ConsoleColor.Magenta).WriteLine();


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

            new ConsoleString(string.Format("Logging in to target {0} ...", this.api), ConsoleColor.Magenta).WriteLine();

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

    public class PushArgs
    {
        [ArgShortcut("--name"), ArgShortcut("-n"), ArgDescription("Name of app"), ArgRequired(PromptIfMissing = true)]
        public string Name { get; set; }

        [ArgShortcut("--memory"), ArgShortcut("-m"), ArgDescription("Amount of memory to allocate"), ArgRequired(PromptIfMissing = true)]
        public int Memory { get; set; }

        [ArgShortcut("--stack"), ArgShortcut("-s"), ArgDescription("Stack to use"), ArgRequired(PromptIfMissing = true)]
        public string Stack { get; set; }

        [ArgShortcut("--dir"), ArgShortcut("-d"), ArgDescription("Directory you want to push"), ArgRequired(PromptIfMissing = true)]
        public string Dir { get; set; }
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
