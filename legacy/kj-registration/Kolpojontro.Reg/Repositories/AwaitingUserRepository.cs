using Kolpojontro.Reg.Core.Repositories;
using Kolpojontro.Reg.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Kolpojontro.Reg.Repositories
{
    public class AwaitingUserRepository : IAwaitingUserRepository
    {
        private readonly ApplicationDbContext _applicationDbContext;

        public AwaitingUserRepository(ApplicationDbContext applicationDbContext)
        {
            _applicationDbContext = applicationDbContext;
        }

        private IQueryable<AwaitingUser> GetAllAwaitingUsers()
        {
            return _applicationDbContext.AwaitingUsers.AsQueryable();
        }

        public async Task<AwaitingUser> CreateUserAsync(AwaitingUser awaitingUser)
        {
            if (awaitingUser == null)
                throw new NullReferenceException("User is null");

            var result = _applicationDbContext.AwaitingUsers.Add(awaitingUser);
            await _applicationDbContext.SaveChangesAsync();
            
            return result.Entity;
        }

        public async Task<List<AwaitingUser>> GetAwaitingUsers()
        {
            var result = await _applicationDbContext.AwaitingUsers.ToListAsync();
            if(result.Count > 0)
            {
                return result;
            }

            return null;
        }

        public async Task<AwaitingUser> GetUserByIdAsync(int Id)
        {
            var result = await _applicationDbContext.AwaitingUsers.FindAsync(Id);

            if (result != null) {
                return result;
            }

            return null;
        }

        public async Task<List<AwaitingUser>> GetAwaitingUsersByStatus(string status)
        {
            var results = await _applicationDbContext.AwaitingUsers.Where(awuser => awuser.Status == status).ToListAsync();

            if (results != null)
            {
                return results;
            }

            return null;
        }

        public async Task<AwaitingUser> UpdateAwaitingUser(AwaitingUser awaitingUser)
        {
            if(awaitingUser == null)
            {
                throw new ArgumentNullException("Invalid Argument");
            }

            var result = _applicationDbContext.AwaitingUsers.Update(awaitingUser);
            await _applicationDbContext.SaveChangesAsync();
            
            return null;
        }

        public async Task<AwaitingUser> DeleteAwaitingUser(int Id)
        {
            var user = await _applicationDbContext.AwaitingUsers.FindAsync(Id);

            if(user != null)
            {
                var result = _applicationDbContext.AwaitingUsers.Remove(user);
                await _applicationDbContext.SaveChangesAsync();
                return result.Entity;
            }

            return null;
        }
    }
}
