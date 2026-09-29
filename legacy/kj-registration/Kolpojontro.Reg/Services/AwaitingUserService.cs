using AutoMapper;
using Kolpojontro.Reg.Core;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Repositories;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Services
{
    public class AwaitingUserService : IAwaitingUserService
    {
        private readonly IAwaitingUserRepository _awaitingUserRepository;
        private readonly IMapper _mapper;

        public AwaitingUserService(IAwaitingUserRepository awaitingUserRepository, IMapper mapper)
        {
            _awaitingUserRepository = awaitingUserRepository;
            _mapper = mapper;
        }

        public async Task<AwaitingUser> CreateUserAsync(AwaitingUserApiResource usermodel)
        {
            if (usermodel == null)
                throw new NullReferenceException("Invalid Awaiting User");

            var awaitingUser = _mapper.Map<AwaitingUser>(usermodel);
            awaitingUser.Status = EAwaitingUserStatus.Awaiting.ToString();
            awaitingUser.DOB = Convert.ToDateTime(awaitingUser.DateOfBirth);
            awaitingUser.StatusLastUpdatedAt = DateTime.Now;

            var response = await _awaitingUserRepository.CreateUserAsync(awaitingUser);

            return response;
        }

        public async Task<List<AwaitingUserApiResource>> GetAwaitingUsers()
        {
            var result = await _awaitingUserRepository.GetAwaitingUsers();
            if(result!= null && result.Count > 0)
            {
                List<AwaitingUserApiResource> AwaitingUsersAsResource = new List<AwaitingUserApiResource>();
                
                foreach(AwaitingUser user in result)
                {
                    
                    var userToAdd = _mapper.Map<AwaitingUserApiResource>(user);

                    AwaitingUsersAsResource.Add(userToAdd);
                }

                return AwaitingUsersAsResource; 
            }
            return null;
        }
        

        public async Task<AwaitingUserApiResource> GetUserByIdAsync(int Id)
        {
            var response = await _awaitingUserRepository.GetUserByIdAsync(Id);

            if (response != null)
            {
                var result = _mapper.Map<AwaitingUserApiResource>(response);
                
                return result;
            }

            return null;
        }

        //GET Awaiting Users By Status
        public async Task<List<AwaitingUserApiResource>> GetAwaitingUsersByStatus(string status)
        {
            var result = await _awaitingUserRepository.GetAwaitingUsersByStatus(status);

            if (result != null && result.Count > 0)
            {
                List<AwaitingUserApiResource> AwaitingUsersAsResource = new List<AwaitingUserApiResource>();

                foreach (AwaitingUser user in result)
                {

                    var userToAdd = _mapper.Map<AwaitingUserApiResource>(user);

                    AwaitingUsersAsResource.Add(userToAdd);
                }

                return AwaitingUsersAsResource;
            }

            return null;
        }

        public async Task<AwaitingUser> UpdateAwaitingUser(AwaitingUser user)
        {
            if(user != null)
            {
                var result = await _awaitingUserRepository.UpdateAwaitingUser(user);

                if (result != null)
                {
                    return result;
                }

            }

            return null;
        }

        public async Task<AwaitingUser> DeleteAwaitingUser(int Id)
        {
            var result = await _awaitingUserRepository.DeleteAwaitingUser(Id);

            if(result != null)
            {
                return result;
            }

            return null;
        }
    }
}
