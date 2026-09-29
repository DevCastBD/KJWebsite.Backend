using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Config
{
    public class AppSettings
    {
        public string JwtKey { get; set; }
        public string JwtExpireMins { get; set; }
    }
}
