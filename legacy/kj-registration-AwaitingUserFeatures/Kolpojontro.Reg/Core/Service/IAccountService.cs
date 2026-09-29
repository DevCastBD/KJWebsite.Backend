using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Microsoft.AspNetCore.Identity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Service
{
    public interface IAccountService
    {
        Task<UserDTO> RegisterAsync(ApplicationUser applicationUser);
        Task<bool> SignInAsync(ApplicationUser applicationUser);
        Task<bool> SignOutAsync();
        Task<bool> ChangePasswordAsync();
    }
}
