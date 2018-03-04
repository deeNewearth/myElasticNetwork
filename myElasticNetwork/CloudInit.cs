using Microsoft.AspNetCore.Http;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace myElasticNetwork
{
    public partial class Startup
    {
        class Template{
            public string templateData { get; set; }
            public string[] toreplace;
        }

        Template getTemplate(HttpRequest Request)
        {
            var template = "coreos";
            if (Request.Query.ContainsKey("template"))
                template = Request.Query["template"];

            var tmpltFile = Path.GetFullPath($"./ymlTemplates/cloud-config-{template}.yml");
            if (!File.Exists(tmpltFile))
                throw new DisplayableException($"template {template} not found", 
                    new Exception($"looking for file {tmpltFile}"));

            var tmpltParsed = new Template { templateData = File.ReadAllText(tmpltFile) };


            var matches = Regex.Matches(tmpltParsed.templateData, @"\$toreplace_[^\$\s]+\$", RegexOptions.Multiline);
            tmpltParsed.toreplace = matches.Cast<Match>()
            .Select(m => m.Value).Distinct()
            .Select(m => m.Trim('$').Replace("toreplace_", ""))
            .ToArray();

            return tmpltParsed;
        }

        public string handleCloudTemplate(HttpRequest Request)
        {
            Console.WriteLine("got handleCloudTemplate");

            var tmpltFile = Path.GetFullPath($"./ymlTemplates/cloudinit.html");
            if (!File.Exists(tmpltFile))
                throw new DisplayableException($"template cloudinit not found");

            var templateData = File.ReadAllText(tmpltFile);
            var parsed = getTemplate(Request);

            var formfields = string.Join("\n", parsed.toreplace.Select(m =>
                            $"<div>{m}<br/><input  class=\"toreplace\" type=\"text\"  name=\"{m}\" required/></div>"));

            templateData = templateData.Replace("<input class=\"toreplace\" type=\"text\"/>", formfields);

            return templateData;

        }

        public string  handleCloudInit(HttpRequest Request)
        {
            Console.WriteLine("got cloudinit");

            var parsed = getTemplate(Request);

            var notfound = parsed.toreplace.Where(m => !Request.Query.ContainsKey(m)).ToArray();
            if (notfound.Length > 0)
            {
                var needed = string.Join(", ", notfound);
                throw new DisplayableException($"values {needed} required");
            }
                

            foreach (var toreplace in parsed.toreplace)
            {
                parsed.templateData = parsed.templateData.Replace($"$toreplace_{toreplace}$", Request.Query[toreplace]);
            }


            return parsed.templateData;
        }
    }
}
