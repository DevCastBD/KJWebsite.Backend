using Kolpojontro.Reg.Config;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Security.Token
{
    public class TokenHandler : ITokenHandler
    {

        private readonly AppSettings _appSettings;
        public readonly IConfiguration _configuration;


        public TokenHandler(AppSettings appSettings,
                            IConfiguration configuration)
        {
            _appSettings = appSettings;
            _configuration = configuration;
        }

        public string CreateAccessToken(ApplicationUser user)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var key = Encoding.ASCII.GetBytes(_configuration["token:JwtKey"]);

            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(new Claim[]
                {
                    new Claim(ClaimTypes.Email, user.Email),
                    new Claim(ClaimTypes.Role, user.Roles)
                }),

                Expires = DateTime.UtcNow.AddMinutes(Convert.ToDouble(_configuration["token:JwtExpireMins"])),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
            };

            var token = tokenHandler.CreateToken(tokenDescriptor);
            var tokenString = tokenHandler.WriteToken(token);

            //user.applicationUser.PasswordHash = null;

            return tokenString;
        }
    }
}
