using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Kolpojontro.Reg.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class AccountController : Controller
    {

        private readonly IAccountService _accountService;

        public AccountController(IAccountService accountService)
        {
            _accountService = accountService;
        }

        [HttpPost]
        [AllowAnonymous]
        public async Task<IActionResult> Register([FromBody] ApplicationUser applicationUser) {

            if (applicationUser != null && ModelState.IsValid)
            {
                var result = await _accountService.RegisterAsync(applicationUser);
                return Ok(result);
            }
            else {
                return BadRequest();
            }
        }

        [HttpPost]
        public Task<object> Login()
        {
            return null;
        }
    }


}