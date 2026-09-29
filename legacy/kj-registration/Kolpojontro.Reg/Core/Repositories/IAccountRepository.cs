using Kolpojontro.Reg.Core.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Repositories
{
    public interface IAccountRepository
    {
        Task<bool> RegisterAsync(RegisterDTO registerDTO);
        Task<bool> LoginAsync();
        Task<bool> ChangePasswordAsync();
        Task<bool> SignOutAsync();
    }
}
