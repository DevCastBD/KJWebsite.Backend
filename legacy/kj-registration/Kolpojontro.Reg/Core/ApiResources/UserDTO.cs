using Kolpojontro.Reg.Core.Models;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.ApiResources
{
    public class UserDTO
    {
        public IdentityResult Response { get; set; }
        public ApplicationUser applicationUser { get; set; }
        public string Token { get; set; }
    }
}
