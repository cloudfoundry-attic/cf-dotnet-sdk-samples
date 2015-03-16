using CloudFoundry.CloudController.V2;
using CloudFoundry.CloudController.V2.Client.Data;
using CloudFoundry.UAA;
using PowerArgs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
            SetTargetInfoFromFile();

            CloudFoundryClient client = new CloudFoundryClient(new Uri(this.api), new System.Threading.CancellationToken());
            client.Login(this.token).Wait();

            PagedResponseCollection<ListAllAppsResponse> apps = client.Apps.ListAllApps().Result;
            while (apps != null && apps.Properties.TotalResults != 0)
            {
                foreach (var app in apps)
                {
                    new ConsoleString(string.Format("{0}", app.Name), ConsoleColor.Green).WriteLine();
                    new ConsoleString(string.Format("Memory: {0}", app.Memory), ConsoleColor.White).WriteLine();
                    new ConsoleString(string.Format("Buildpack: {0}", app.Buildpack ?? string.Empty), ConsoleColor.White).WriteLine();
                    new ConsoleString("------------------", ConsoleColor.Gray).WriteLine();
                }

                apps = apps.GetNextPage().Result;
            }
        }

        [ArgActionMethod, ArgDescription("List all stacks")]
        public void Stacks()
        {
            SetTargetInfoFromFile();

            CloudFoundryClient client = new CloudFoundryClient(new Uri(this.api), new System.Threading.CancellationToken());
            client.Login(this.token).Wait();

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

        [ArgActionMethod, ArgDescription("Deletes an application")]
        public void DeleteApp(string app, string space)
        {
            SetTargetInfoFromFile();
        }

        [ArgActionMethod, ArgDescription("Push an application to the cloud")]
        public void Push(string path, string stack)
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

        private void SetTargetInfoFromFile()
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
        }

    }

    public class LoginArgs
    {
        [ArgShortcut("--api"), ArgShortcut("-a"), ArgDescription("Target API"), ArgRequired(PromptIfMissing=true)]
        public string Api { get; set; }

        [ArgShortcut("--user"), ArgShortcut("-u"), ArgDescription("CloudFoundry Username"), ArgRequired(PromptIfMissing = true)]
        public string User { get; set; }
        
        [ArgShortcut("--pass"), ArgShortcut("-p"), ArgDescription("CloudFoundry Password")]
        public SecureStringArgument Password { get; set; }

        [ArgShortcut("--skip-ssl"), ArgShortcut("-x"), ArgDescription("Skip ssl validation")]
        public bool SkipSSL { get; set; }
    }


    class Program
    {
        static void PrintExceptionMessage(Exception ex)
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
