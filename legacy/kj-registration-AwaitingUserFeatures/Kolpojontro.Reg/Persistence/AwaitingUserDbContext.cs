using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Persistence
{
    public class AwaitingUserDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public AwaitingUserDbContext(IConfiguration configuration, DbContextOptions<AwaitingUserDbContext> options)
            : base(options)
        {
            _configuration = configuration;
        }

        public DbSet<AwaitingUser> awaitingUsers { get; set; }


    }
}
