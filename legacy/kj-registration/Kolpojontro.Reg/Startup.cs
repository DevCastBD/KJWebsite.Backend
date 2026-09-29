using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutoMapper;
using Kolpojontro.Reg.Config;
using Kolpojontro.Reg.Core;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Repositories;
using Kolpojontro.Reg.Core.Security;
using Kolpojontro.Reg.Core.Security.Hashing;
using Kolpojontro.Reg.Core.Service;
using Kolpojontro.Reg.Persistence;
using Kolpojontro.Reg.Repositories;
using Kolpojontro.Reg.Security.Hashing;
using Kolpojontro.Reg.Security.Token;
using Kolpojontro.Reg.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TokenHandler = Kolpojontro.Reg.Security.Token.TokenHandler;

namespace Kolpojontro.Reg
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            // ============= Global Config from AppSettings ==============
            services.Configure<AppSettings>(Configuration.GetSection("token"));
            
            //============== Db Context ============
            services.AddDbContext<ApplicationDbContext>();

            // ============= Add Repositories ==========
            services.AddScoped<IAwaitingUserRepository, AwaitingUserRepository>();


            // ============= Add Services ===========
            services.AddScoped<IAccountService, AccountService>();
            services.AddScoped<IAwaitingUserService, AwaitingUserService>();
            services.AddScoped<ICommonService, CommonService>();
            services.AddScoped<IPasswordHasher, PasswordHasher>();
            services.AddScoped<AppSettings>();
            services.AddScoped<ITokenHandler, TokenHandler>();

            //============== Add Identity ==========
            services.AddIdentity<ApplicationUser, IdentityRole>()
                    .AddRoles<IdentityRole>()
                    .AddEntityFrameworkStores<ApplicationDbContext>()
                    .AddDefaultTokenProviders();

            //======= Add AutoMapper ========
            var autoMapperConfig = new MapperConfiguration(cfg =>
            {
                // for mapping FROM AwaitingUserApiResource TO AwaitingUser
                cfg.CreateMap<AwaitingUserApiResource, AwaitingUser>()
                    .ForMember(x => x.FirstName, opt => opt.MapFrom(a => a.FirstName))
                    .ForMember(x => x.LastName, opt => opt.MapFrom(a => a.LastName))
                    .ForMember(x => x.Gender, opt => opt.MapFrom(a => a.Gender))
                    .ForMember(x => x.ReasonForJoining, opt => opt.MapFrom(a => a.ReasonForJoining))
                    .ForMember(x => x.PresentOrganization, opt => opt.MapFrom(a => a.PresentOrganization))
                    .ForMember(x => x.VolunteeingExperience, opt => opt.MapFrom(a => a.VolunteeingExperience))
                    .ForMember(x => x.DateOfBirth, opt => opt.MapFrom(a => a.DateOfBirth))
                    .ForMember(x => x.CityOfResidence, opt => opt.MapFrom(a => a.CityOfResidence))
                    .ForMember(x => x.CountryOfResidence, opt => opt.MapFrom(a => a.CountryOfResidence))
                    .ForMember(x => x.Id, opt => opt.Ignore())
                    //.ForMember(x => x.Id, opt => opt.Ignore())
                    .ForMember(x => x.DOB, opt => opt.Ignore())
                    .ForMember(x => x.Status, opt => opt.Ignore())
                    .ForMember(x => x.StatusLastUpdatedAt, opt => opt.Ignore());

                // for mapping FROM AwaitingUser TO AwaitingUserApiResource
                cfg.CreateMap<AwaitingUser, AwaitingUserApiResource>()
                    .ForMember(x => x.FirstName, opt => opt.MapFrom(a => a.FirstName))
                    .ForMember(x => x.LastName, opt => opt.MapFrom(a => a.LastName))
                    .ForMember(x => x.Gender, opt => opt.MapFrom(a => a.Gender))
                    .ForMember(x => x.ReasonForJoining, opt => opt.MapFrom(a => a.ReasonForJoining))
                    .ForMember(x => x.PresentOrganization, opt => opt.MapFrom(a => a.PresentOrganization))
                    .ForMember(x => x.VolunteeingExperience, opt => opt.MapFrom(a => a.VolunteeingExperience))
                    .ForMember(x => x.DateOfBirth, opt => opt.MapFrom(a => a.DateOfBirth))
                    .ForMember(x => x.CityOfResidence, opt => opt.MapFrom(a => a.CityOfResidence))
                    .ForMember(x => x.CountryOfResidence, opt => opt.MapFrom(a => a.CountryOfResidence))
                    .ForMember(x => x.Id, opt => opt.MapFrom(a => a.Id))
                    .ForSourceMember(x => x.DOB, opt => opt.DoNotValidate())
                    .ForSourceMember(x => x.Status, opt => opt.DoNotValidate())
                    .ForSourceMember(x => x.StatusLastUpdatedAt, opt => opt.DoNotValidate());
            });

            // only during development, validate your mappings; remove it before release
            autoMapperConfig.AssertConfigurationIsValid();

            var mapper = autoMapperConfig.CreateMapper();

            services.AddSingleton(mapper);

            var key = Encoding.ASCII.GetBytes(Configuration["token:JwtKey"]);
            services.AddAuthentication(x =>
            {
                x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(x =>
            {
                x.RequireHttpsMetadata = false;
                x.SaveToken = true;
                x.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(key),
                    ValidateIssuer = false,
                    ValidateAudience = false
                };
            });

            //======= Add MVC =======
            services.AddMvc().SetCompatibilityVersion(CompatibilityVersion.Version_2_2);

            //======= Add HATEOAS ======
            
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, 
                                IHostingEnvironment env,
                                ApplicationDbContext applicationDbContext)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }
            else
            {
                // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
                app.UseHsts();
            }

            // ===== Use Authentication ======
            app.UseAuthentication();
            app.UseHttpsRedirection();
            app.UseMvc();


            // ==== Create Db =====
            applicationDbContext.Database.EnsureCreated();
        }
    }
}
