using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Repositories
{
    public interface IAwaitingUserRepository
    {
        Task<AwaitingUser> CreateUserAsync(AwaitingUser awaitingUser);
        Task<AwaitingUser> GetUserByIdAsync(int Id);
        Task<List<AwaitingUser>> GetAwaitingUsers();
        Task<List<AwaitingUser>> GetAwaitingUsersByStatus(string status);
        Task<AwaitingUser> UpdateAwaitingUser(AwaitingUser awaitingUser);
        Task<AwaitingUser> DeleteAwaitingUser(int Id);
    }
}
