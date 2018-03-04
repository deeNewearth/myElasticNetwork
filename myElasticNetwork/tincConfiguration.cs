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

        /// <summary>
        /// is used mainly to turn on the -d3 flag for debugging. It's needed cause we run this container with start always 
        /// so imposibe to update it for tests
        /// </summary>
        public string tincExtraParams { get; set; }
    }
}
