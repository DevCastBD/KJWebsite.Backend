using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Repositories
{
    public class AccountRepository : IAccountRepository
    {
        public Task<bool> ChangePasswordAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> LoginAsync()
        {
            throw new NotImplementedException();
        }

        public Task<bool> RegisterAsync(RegisterDTO registerDTO)
        {
            throw new NotImplementedException();
        }

        public Task<bool> SignOutAsync()
        {
            throw new NotImplementedException();
        }
    }
}
