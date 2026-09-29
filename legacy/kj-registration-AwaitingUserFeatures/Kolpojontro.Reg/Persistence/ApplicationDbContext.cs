using Kolpojontro.Reg.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Persistence
{
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>
    {
        public IConfiguration _configuration;

        public ApplicationDbContext(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public DbSet<AwaitingUser> AwaitingUsers { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            //base.OnConfiguring(optionsBuilder);
            optionsBuilder.UseMySql(GetConnectionString());

        }

        private string GetConnectionString()
        {
            string HostName = _configuration.GetSection("ConnectionString:Host").Value;
            string DatabaseName = _configuration.GetSection("ConnectionString:DatabaseName").Value;
            string UserName = _configuration.GetSection("ConnectionString:DatabaseUser").Value;
            string DatabasePassword = _configuration.GetSection("ConnectionString:DatabasePassword").Value;
            
            return $"Server={HostName};" +
                   $"database={DatabaseName};" +
                   $"uid={UserName};" +
                   $"pwd={DatabasePassword};" +
                   $"pooling=true;";
        }
    }
}
