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
    class TincHandler
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

        public Process readConfigAndStart(bool restart = false)
        {
            Console.WriteLine("Updating network parameters");


            
            Console.WriteLine($"Current server name :{_config.serverName}");

            if (string.IsNullOrWhiteSpace(_config.serverName))
                throw new Exception("servename is required");

            if (string.IsNullOrWhiteSpace(_config.serverPrivateIp))
                throw new Exception("serverPrivateIp is required");

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

            var myConfigChanged = false;
            var updateRegistry = false;

            if (!serverMap.ContainsKey(_config.serverName))
            {
                Console.WriteLine($"server name :{_config.serverName}  not found creating");

                serverMap[_config.serverName] = new CoreServer
                {
                    //ordinalNumber = newOrdinal,
                    isManager = _config.isManager,
                    initialPublicIp = _config.serverPublicIp,
                    serverPrivateIp = _config.serverPrivateIp
                };
                updateRegistry = true;

            }

            if (serverMap[_config.serverName].initialPublicIp != _config.serverPublicIp)
            {
                serverMap[_config.serverName].initialPublicIp = _config.serverPublicIp;
                updateRegistry = true;
            }

            if (updateRegistry)
            {
                saveS3Object(_SERVER_REGISTRY, JsonConvert.SerializeObject(serverMap));
                myConfigChanged = true;
            }


            if (serverMap[_config.serverName].serverPrivateIp != _config.serverPrivateIp)
                throw new Exception("found serverIP {serverMap[serverName].serverPrivateIp} from registry doesn't match provided {serverPrivateIp}");


            var confData = new string[] { $"Name = {_config.serverName}", "AddressFamily = ipv4", "Interface = tun0" };

            {
                var otherServers = serverMap.Where(v => v.Key != _config.serverName && !String.IsNullOrWhiteSpace(v.Value.initialPublicIp))
                    .Select(m => $"ConnectTo = {m.Key}").ToArray();

                confData = confData.Concat(otherServers).ToArray();
            }

            if (String.IsNullOrWhiteSpace(_config.tincExecutable))
            {
                throw new Exception("tinc path needed");
            }

            if (!Directory.Exists(_confLocation))
                Directory.CreateDirectory(_confLocation);

            var hostConfFileWIthPublicKeys = Path.Combine(_confLocation, "hosts", _config.serverName);

            if(!File.Exists(hostConfFileWIthPublicKeys))
            {
                myConfigChanged = true;
                var iinitCmd = $"init {_config.serverName}";
                Console.WriteLine($"Running :{_config.tincExecutable} {iinitCmd} ");
                var process = Process.Start(_config.tincExecutable, iinitCmd);
                process.WaitForExit();
                Console.WriteLine($"completed : {iinitCmd} ");
            }


            if (!restart)
            {
                var confFile = Path.Combine(_confLocation, "tinc.conf");

                Console.WriteLine($"Writing conf file {confFile}");
                File.WriteAllLines(confFile, confData);
            }

            {
                
                Console.WriteLine($"Updating file {hostConfFileWIthPublicKeys}");
                if (!File.Exists(hostConfFileWIthPublicKeys))
                    throw new Exception($"file {hostConfFileWIthPublicKeys} not found. most likely tinc init failed.");

                var hostInfo = new string[] { };
                
                if (!String.IsNullOrWhiteSpace(_config.serverPublicIp))
                {
                    hostInfo = new string[] { $"Address = {_config.serverPublicIp}" };
                }

                hostInfo = hostInfo.Concat(new[] { $"Subnet = {_config.serverPrivateIp}/32" }).ToArray();

                var KeyInfo = File.ReadAllLines(hostConfFileWIthPublicKeys);

                var keyStrated = false;
                KeyInfo = KeyInfo.Select(s =>
                {
                    if (!!String.IsNullOrWhiteSpace(_config.serverPublicIp) && s.StartsWith("Address = "))
                    {
                        var currentAddress = s.Replace("Address = ", "");
                        if (currentAddress != _config.serverPublicIp)
                        {
                            myConfigChanged = true;
                            Console.WriteLine("public address changed will trigger change request");
                        }
                    }

                    if (!keyStrated && !s.Contains("BEGIN RSA PUBLIC KEY"))
                        return null;
                    keyStrated = true;
                    return s;
                })
                .Where(s=>null!=s)
                .ToArray();

                hostInfo = hostInfo.Concat(KeyInfo).ToArray();

                File.WriteAllLines(hostConfFileWIthPublicKeys, hostInfo);
                Console.WriteLine($"done with {hostConfFileWIthPublicKeys}. Updating to registry");

                saveS3Object(key: $"{_SERVER_HOST_FOLDER}{_config.serverName}", content: String.Join('\n', hostInfo));

                Console.WriteLine("done uploading");
            }

            {
                Console.WriteLine("getting other hosts");
                var otherServersValues = serverMap.Where(v => v.Key != _config.serverName).Select(v=>
                {
                    var ohostdata = readS3Object($"{_SERVER_HOST_FOLDER}{v.Key}");
                    return new { v.Key, data = ohostdata.value };
                }).ToArray();

                foreach(var v in otherServersValues)
                {
                    var hostConf = Path.Combine(_confLocation, "hosts", v.Key);
                    Console.WriteLine($"writing  host info  :{hostConf}");
                    File.WriteAllText(hostConf, v.data);
                }

                Console.WriteLine("getting other hosts done");

            }

            if(!restart)
            {
                var tincupfile = Path.Combine(_confLocation, "tinc-up");
                Console.WriteLine($"creating file {tincupfile}");

                var fileText = new[] { "#!/bin/sh", $"ifconfig $INTERFACE {_config.serverPrivateIp} netmask 255.255.255.0" };
                File.WriteAllLines(tincupfile, fileText);
                Console.WriteLine($"done with {tincupfile}");
            }

            if (myConfigChanged)
            {
                var otherServersValues = serverMap.Where(v => v.Key != _config.serverName
                        && !String.IsNullOrWhiteSpace(v.Value.initialPublicIp)).Select(v=>v.Value.initialPublicIp).ToArray();

                foreach(var publicserer in otherServersValues)
                {
                    try
                    {
                        var done = new System.Net.WebClient().DownloadString($"http://{publicserer}:{Program._myPort}/configchanged");
                    }
                    catch (Exception ex)
                    {
                        ex.PrintDetails($"failed while change notifying :{publicserer}:{Program._myPort}");
                    }
                }
                    
            }

            {
                //https://www.tinc-vpn.org/documentation-1.1/tinc-commands.html

                var initCmd = $"start -D " + string.Join(' ',_tincARgs);


                if (!String.IsNullOrWhiteSpace(_config.tincExtraParams))
                {
                    initCmd += (" " + _config.tincExtraParams);
                }

                if(restart)
                    initCmd = "restart";

                Console.WriteLine($"Running :{_config.tincExecutable} {initCmd} ");

                return Process.Start(_config.tincExecutable, initCmd);
                
                
            }

        }

        
        class S3Object
        {
            public string value { get; set; }
            public DateTime lastModified { get; set; }
        }

        void saveS3Object(string key, string content)
        {
            Console.WriteLine($"saving s3Object {_config.s3BucketName} : {key}");
            var putRequest1 = new PutObjectRequest
            {
                BucketName = _config.s3BucketName,
                Key = key,
                ContentBody = content,
            };

            PutObjectResponse response1 = _s3client.PutObjectAsync(putRequest1).Result;
            Console.WriteLine($"Saved s3Object {_config.s3BucketName} : {key}");
        }

        

        S3Object readS3Object(string key)
        {

            if (null == _s3client)
            {
                if (String.IsNullOrWhiteSpace(_config.s3AccessId) )
                    throw new Exception("s3AccessId is required");
                if (String.IsNullOrWhiteSpace(_config.s3AccessKey) )
                    throw new Exception("s3AccessKey is required");
                if (String.IsNullOrWhiteSpace(_config.s3BucketName) )
                    throw new Exception("s3BucketName is required");


                Console.WriteLine($"getting s3Object {_config.s3BucketName} : {key}");


                _s3client = new AmazonS3Client(
                    awsAccessKeyId: _config.s3AccessId,
                    awsSecretAccessKey: _config.s3AccessKey,
                    region: Amazon.RegionEndpoint.USWest2);
            }



            var request = new GetObjectRequest
            {
                BucketName = _config.s3BucketName,
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

        
        
        readonly string[] _tincARgs;

        readonly tincConfiguration _config;
        readonly string _confLocation;

        public TincHandler(string[] tincARgs)
        {
            _tincARgs = tincARgs;


            var envVariables = Environment.GetEnvironmentVariables();

            _confLocation = "/etc/tinc";

            if (envVariables.Contains("confLocation"))
                _confLocation = envVariables["confLocation"].ToString();

            var fullSettingsPath = Path.GetFullPath(Path.Combine(_confLocation,"appsettings.json"));
            Console.WriteLine($"setting path {fullSettingsPath}");

            var overrideSettingsPath = Path.GetFullPath(Path.Combine(_confLocation, "appsettings.override.json"));
            if (File.Exists(overrideSettingsPath))
            {
                Console.WriteLine("setting override exists using override ");
                fullSettingsPath = overrideSettingsPath;
            }

            Console.WriteLine($"reading appsettings from {fullSettingsPath}");

            var myConfigStr = File.ReadAllText(fullSettingsPath);
            _config = JsonConvert.DeserializeObject<tincConfiguration>(myConfigStr);

            if (String.IsNullOrWhiteSpace(_config.tincExecutable))
                _config.tincExecutable = "/usr/sbin/tinc";
        }

    }
}
