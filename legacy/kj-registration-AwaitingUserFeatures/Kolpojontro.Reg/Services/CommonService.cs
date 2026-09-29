using Kolpojontro.Reg.Core.Service;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Services
{
    public class CommonService : ICommonService
    {
        public string Capitalize(string input)
        {
            if (string.IsNullOrEmpty(input)) {
                return null;
            }

            return input.First().ToString().ToUpper() + input.Substring(1);
        }
    }
}
