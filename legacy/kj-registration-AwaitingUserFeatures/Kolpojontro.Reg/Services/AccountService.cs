using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Repositories;
using Kolpojontro.Reg.Core.Service;
using System;
using Microsoft.AspNetCore.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Mvc;
using Kolpojontro.Reg.Core.Security.Hashing;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Security;

namespace Kolpojontro.Reg.Services
{
    public class AccountService : IAccountService
    {
        //public readonly IAccountRepository _accountRepository;
        public readonly UserManager<ApplicationUser> _userManager;
        public readonly SignInManager<ApplicationUser> _signInManager;
        public readonly RoleManager<IdentityRole> _roleManager;
        public readonly IConfiguration _configuration;
        public readonly IPasswordHasher _passwordHasher;
        public readonly ITokenHandler _tokenHandler;
        

        public AccountService(UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            RoleManager<IdentityRole> roleManager,
            IConfiguration configuration,
            IPasswordHasher passwordHasher,
            ITokenHandler tokenHandler)
        {
            //_accountRepository = accountRepository;
            _userManager = userManager;
            _signInManager = signInManager;
            _roleManager = roleManager;
            _configuration = configuration;
            _passwordHasher = passwordHasher;
            _tokenHandler = tokenHandler;
        }

        public Task<bool> ChangePasswordAsync()
        {
            throw new NotImplementedException();
        }

        public async Task<bool> SignInAsync(ApplicationUser applicationUser)
        {
            try
            {
                await _signInManager.SignInAsync(applicationUser, true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return false;
            }

            throw new NotImplementedException();
        }

        public async Task<UserDTO> RegisterAsync(ApplicationUser applicationUser)
        {
            IdentityResult result = new IdentityResult();

            try {

                var roles = applicationUser.Roles.Split(",");

                if (roles.Count() > 0)
                {
                   foreach(var role in roles)
                    {
                        if (!Enum.IsDefined(typeof(ERoles), role))
                        {
                            return null;
                        }
                        else if (Enum.IsDefined(typeof(ERoles), role))
                        {
                            var roleExist = await _roleManager.RoleExistsAsync(role);
                            if(!roleExist)
                                await _roleManager.CreateAsync(new IdentityRole(role));
                        }
                        result = await _userManager.CreateAsync(applicationUser, _passwordHasher.HashPassword(applicationUser.PasswordHash));

                        await _userManager.AddToRoleAsync(applicationUser, role);
                    }

                }

                var user = await _userManager.FindByEmailAsync(applicationUser.Email);
                var token = _tokenHandler.CreateAccessToken(user);

                var response = new UserDTO
                {
                    applicationUser = user,
                    Response = result,
                    Token = token
                };

                

                return response;

            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return null;
            }
        }

        public Task<bool> SignOutAsync()
        {
            throw new NotImplementedException();
        }
    }
}
