using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Amazon.S3;
using Amazon.S3.Model;
using Newtonsoft.Json;
using System.Linq;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace myElasticNetwork
{
    public class DisplayableException : Exception
    {
        public DisplayableException(string message, Exception inner = null)
            : base(message, inner) { }
    }

    public static class Program
    {
        internal static TincHandler _tincHandler = null;

        
        public static int Main(string[] args)
        {
            try
            {
                var argumetString = null == args ? "" : string.Join(", ", args);
                Console.Write($"Version : {typeof(Program).Assembly.GetName().Version}, argumets: {argumetString}");

                _tincHandler = new TincHandler(args);

                var tincProcess = _tincHandler.readConfigAndStart();
                //don't block we kill the prcess if tinc dies
                BuildWebHost().RunAsync();


                tincProcess.WaitForExit();
                Console.WriteLine("tinc process completed");

                return 0;
            }
            catch (Exception ex)
            {
                ex.PrintDetails();
                return -1;

            }
        }

        public static void PrintDetails(this Exception ex, string reason ="Exception")
        {
            Console.WriteLine($"{reason}:{ex.Message}");
            var inner = ex.InnerException;
            while (null != inner)
            {
                Console.WriteLine($" -{inner.Message}");
                inner = inner.InnerException;
            }
        }

        public static int _myPort = 8051;
        public static IWebHost BuildWebHost()
        {
            var envVariables = Environment.GetEnvironmentVariables();

            var webPort = envVariables.Contains("webPort") ? envVariables["webPort"].ToString():null;

            
            if (!String.IsNullOrWhiteSpace(webPort))
            {
                _myPort = int.Parse(webPort);
            }

            Console.WriteLine($"listening on port {_myPort}");

            return WebHost.CreateDefaultBuilder( )
                .UseStartup<Startup>()
                .UseKestrel(options =>
                {
                    //options.Listen(IPAddress.Loopback, _myPort);
                    options.Listen(IPAddress.Any, _myPort);
                })
                .Build();
        }
    }

    public partial class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            app.Run(context =>
            {
                object ret = new { error = "not found" };
                

                
                context.Response.ContentType = "application/json";

                try
                {

                    switch (context.Request.Path.Value.ToLowerInvariant().TrimStart('/'))
                    {
                        case "configchanged":
                            Console.WriteLine("got change request");
                            Console.WriteLine("reloading Tinc");

                            //since dockecr process waits on tinc working and we set the contaner to start if stopped.
                            //we simply stop this so it refreshes
                            Program._tincHandler.readConfigAndStart(true);

                            ret = new { done = true };
                            break;

                        case "version":
                            ret = new { version = typeof(Program).Assembly.GetName().Version.ToString() };
                            break;

                        case "cloudinit":
                            ret = handleCloudInit(context.Request);
                            context.Response.ContentType = "text/plain";
                            break;

                        case "cloudinitform":
                            ret = handleCloudTemplate(context.Request);
                            context.Response.ContentType = "text/html";
                            break;

                        default:
                            context.Response.StatusCode = 404;
                            break;
                    }
                }
                catch (DisplayableException ex)
                {
                    ex.PrintDetails("failed");
                    context.Response.StatusCode = 400;
                    ret = new { error = $"failed cloudInit: {ex.Message}" };
                }
                catch (Exception ex)
                {
                    ex.PrintDetails("failed");
                    context.Response.StatusCode = 500;
                    ret = new { error = $"failed cloudInit" };
                }


                

                return context.Response.WriteAsync("application/json" == context.Response.ContentType ? 
                                                            JsonConvert.SerializeObject(ret):ret.ToString());
            });
        }
    }
}
