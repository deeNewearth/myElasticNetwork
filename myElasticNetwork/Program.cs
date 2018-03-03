using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Amazon.S3;
using Amazon.S3.Model;
using Newtonsoft.Json;
using System.Linq;
using System.Diagnostics;

namespace myElasticNetwork
{
    class Program
    {
        IAmazonS3 _s3client = null;

        /// <summary>
        /// a serer in my network
        /// </summary>
        class CoreServer
        {
            public bool isManager { get; set; }

            //this is used to create network entry we make sure this is always enique
            //public int ordinalNumber { get; set; }

            /// <summary>
            /// note this might not be the current Public IP
            /// </summary>
            public string initialPublicIp { get; set; }

            public string serverPrivateIp { get; set; }
        }

        class ServerMap : Dictionary<string, CoreServer> { }

        readonly static string _SERVER_REGISTRY ="servers/registry.json";
        readonly static string _SERVER_HOST_FOLDER = "servers/hosts/";

        void updateNetworkParams()
        {
            Console.WriteLine("Updating network parameters");
            var envVariables = Environment.GetEnvironmentVariables();

            var serverName = envVariables["serverName"].ToString();
            var serverPublicIp = envVariables["serverPublicIp"].ToString();
            var isManager = envVariables["isManager"].ToString();

            var serverPrivateIp = envVariables["serverPrivateIp"].ToString();
            if (String.IsNullOrWhiteSpace(serverPrivateIp) || "none" == serverPrivateIp)
                throw new Exception("serverPrivateIp is required");

            if (String.IsNullOrWhiteSpace(serverName) || "none" == serverName)
                throw new Exception("serverName is required");

            Console.WriteLine($"Current server name :{serverName}");

            S3Object serverRegistry = null;
            try
            {
                serverRegistry  = readS3Object(_SERVER_REGISTRY);
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("The specified key does not exist"))
                {
                    Console.WriteLine($"no registryfile {_SERVER_REGISTRY}");
                }
                else
                    throw ex;
            }
            

            var serverMap = null==serverRegistry ? new ServerMap() :
                JsonConvert.DeserializeObject<ServerMap>(serverRegistry.value);

            Console.WriteLine($"Got server map, we have {serverMap.Count} servers");

            if (!serverMap.ContainsKey(serverName))
            {
                Console.WriteLine($"server name :{serverName}  not found creating");

                serverMap[serverName] = new CoreServer
                {
                    //ordinalNumber = newOrdinal,
                    isManager = isManager == "true",
                    initialPublicIp = "none"!= serverPublicIp? serverPublicIp:null,
                    serverPrivateIp = serverPrivateIp
                };

                saveS3Object(_SERVER_REGISTRY, JsonConvert.SerializeObject(serverMap));
            }
            else
            {
                if (serverMap[serverName].serverPrivateIp != serverPrivateIp)
                    throw new Exception("found serverIP {serverMap[serverName].serverPrivateIp} from registry doesn't match provided {serverPrivateIp}");
            }

            var confData = new string[] { $"Name = {serverName}", "AddressFamily = ipv4", "Interface = tun0" };

            {
                var otherServers = serverMap.Where(v => v.Key != serverName && !String.IsNullOrWhiteSpace(v.Value.initialPublicIp))
                    .Select(m => $"ConnectTo = {m.Key}").ToArray();

                confData = confData.Concat(otherServers).ToArray();
            }

            if (String.IsNullOrWhiteSpace(_TincProgram))
            {
                throw new Exception("tinc path needed");
            }

                
            {
                var iinitCmd = $"init {serverName}";
                Console.WriteLine($"Running :{_TincProgram} {iinitCmd} ");
                var process = Process.Start(_TincProgram, iinitCmd);
                process.WaitForExit();
                Console.WriteLine($"completed : {iinitCmd} ");
            }

            if (!Directory.Exists(_confFolder))
                Directory.CreateDirectory(_confFolder);

            {
                var confFile = Path.Combine(_confFolder, "tinc.conf");

                Console.WriteLine($"Writing conf file {confFile}");
                File.WriteAllLines(confFile, confData);
            }

            {
                var hostConf = Path.Combine(_confFolder, "hosts", serverName);
                Console.WriteLine($"Updating file {hostConf}");
                if (!File.Exists(hostConf))
                    throw new Exception($"file {hostConf} not found. most likely tinc init failed.");

                var hostInfo = (String.IsNullOrWhiteSpace(serverPublicIp) || "none" == serverPublicIp) ?
                            new string[] { } : new string[] { $"Address = {serverPublicIp}" };

                hostInfo = hostInfo.Concat(new[] { $"Subnet = {serverPrivateIp}/32" }).ToArray();

                var KeyInfo = File.ReadAllLines(hostConf);

                var keyStrated = false;
                KeyInfo = KeyInfo.Select(s =>
                {
                    if (!keyStrated && !s.Contains("BEGIN RSA PUBLIC KEY"))
                        return null;
                    keyStrated = true;
                    return s;
                })
                .Where(s=>null!=s)
                .ToArray();

                hostInfo = hostInfo.Concat(KeyInfo).ToArray();

                File.WriteAllLines(hostConf, hostInfo);
                Console.WriteLine($"done with {hostConf}. Updating to registry");

                saveS3Object(key: $"{_SERVER_HOST_FOLDER}{serverName}", content: String.Join('\n', hostInfo));

                Console.WriteLine("done uploading");
            }

            {
                Console.WriteLine("getting other hosts");
                var otherServersValues = serverMap.Where(v => v.Key != serverName).Select(v=>
                {
                    var ohostdata = readS3Object($"{_SERVER_HOST_FOLDER}{v.Key}");
                    return new { v.Key, data = ohostdata.value };
                }).ToArray();

                foreach(var v in otherServersValues)
                {
                    var hostConf = Path.Combine(_confFolder, "hosts", v.Key);
                    Console.WriteLine($"writing  host info  :{hostConf}");
                    File.WriteAllText(hostConf, v.data);
                }

                Console.WriteLine("getting other hosts done");

            }

            {
                var tincupfile = Path.Combine(_confFolder, "tinc-up");
                Console.WriteLine($"creating file {tincupfile}");

                var fileText = new[] { "#!/bin/sh", $"ifconfig $INTERFACE {serverPrivateIp} netmask 255.255.255.0" };
                File.WriteAllLines(tincupfile, fileText);
                Console.WriteLine($"done with {tincupfile}");
            }

            {
                var iinitCmd = $"start -D " + string.Join(' ',_tincARgs);
                Console.WriteLine($"Running :{_TincProgram} {iinitCmd} ");
                var process = Process.Start(_TincProgram, iinitCmd);
                process.WaitForExit();
                Console.WriteLine($"completed : {iinitCmd} ");
            }

        }

        class S3Object
        {
            public string value { get; set; }
            public DateTime lastModified { get; set; }
        }

        void saveS3Object(string key, string content)
        {
            Console.WriteLine($"saving s3Object {_s3BucketName} : {key}");
            var putRequest1 = new PutObjectRequest
            {
                BucketName = _s3BucketName,
                Key = key,
                ContentBody = content,
            };

            PutObjectResponse response1 = _s3client.PutObjectAsync(putRequest1).Result;
            Console.WriteLine($"Saved s3Object {_s3BucketName} : {key}");
        }

        string _s3BucketName = null;

        S3Object readS3Object(string key)
        {

            if (null == _s3client)
            {
                var envVariables = Environment.GetEnvironmentVariables();
                _s3BucketName = envVariables["s3BucketName"].ToString();

                Console.WriteLine($"getting s3Object {_s3BucketName} : {key}");

                var s3AccessId = envVariables["s3AccessId"].ToString();
                var s3AccessKey = envVariables["s3AccessKey"].ToString();

                if (String.IsNullOrWhiteSpace(s3AccessId) || "none" == s3AccessId)
                    throw new Exception("s3AccessId is required");
                if (String.IsNullOrWhiteSpace(s3AccessKey) || "none" == s3AccessKey)
                    throw new Exception("s3AccessKey is required");
                if (String.IsNullOrWhiteSpace(_s3BucketName) || "none" == _s3BucketName)
                    throw new Exception("s3BucketName is required");


                _s3client = new AmazonS3Client(
                    awsAccessKeyId: s3AccessId,
                    awsSecretAccessKey: s3AccessKey,
                    region: Amazon.RegionEndpoint.USWest2);
            }



            var request = new GetObjectRequest
            {
                BucketName = _s3BucketName,
                Key = key
            };

            using (var response = _s3client.GetObjectAsync(request).Result)
            using (var responseStream = response.ResponseStream)
            using (var reader = new StreamReader(responseStream))
            {
                string lastModifiedStr = response.Metadata["Last-Modified"];

                var ret= new S3Object
                {
                    value = reader.ReadToEnd(),
                   // lastModified = DateTime.Parse(lastModifiedStr)

                };

                Console.WriteLine($"Got s3Object length:{ret.value.Length}, last modified {lastModifiedStr}");

                return ret;
            }
        }

        readonly String _confFolder;
        readonly String _TincProgram;
        readonly string[] _tincARgs;

        Program(string nextProgram, String confFolder, string[] tincARgs)
        {
            _confFolder = confFolder;
            _TincProgram = nextProgram;
            _tincARgs = tincARgs;

        }

        static int Main(string[] args)
        {

            try
            {
                Console.WriteLine($"Version : {typeof(Program).Assembly.GetName().Version}");

                //args order is string nextProgram, String confFolder
                var nextProgram = "/usr/sbin/tinc";
                var confFolder = "/etc/tinc";

                var tincArgs = new string[] { };

                if (null != args)
                {
                    tincArgs = args;
                    var i = 0;
                    foreach (var a in args)
                    {
                        /*if (0==i)
                            nextProgram = args[i];
                        else if (1==i)
                            confFolder = args[i];
                        */

                        Console.WriteLine($"argument : {i++} :{a}");
                    }
                }

                var p = new Program(nextProgram, confFolder, tincArgs);
                p.updateNetworkParams();
                

                Console.WriteLine("done");

               


                return 0;

            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed with Exception :{ex.Message}");
                var inner = ex.InnerException;
                while (null != inner)
                {
                    Console.WriteLine($" -{inner.Message}");
                    inner = inner.InnerException;
                }

                return -1;
            }
        }
    }
}
