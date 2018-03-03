using System;
using System.Collections.Generic;
using System.Text;

namespace myElasticNetwork
{
    public class tincConfiguration
    {
        public string serverName { get; set; }
        public string serverPublicIp { get; set; }
        public bool isManager { get; set; }
        public string serverPrivateIp { get; set; }

        public string s3BucketName { get; set; }
        public string s3AccessId { get; set; }
        public string s3AccessKey { get; set; }

        public string tincExecutable { get; set; }
    }
}
