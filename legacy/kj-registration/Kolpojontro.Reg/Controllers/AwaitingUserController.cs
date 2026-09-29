using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Kolpojontro.Reg.Core;
using Kolpojontro.Reg.Core.ApiResources;
using Kolpojontro.Reg.Core.Models;
using Kolpojontro.Reg.Core.Responses;
using Kolpojontro.Reg.Core.Service;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;


namespace Kolpojontro.Reg.Controllers
{
    [AllowAnonymous]
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class AwaitingUserController : Controller
    {
        private readonly IAwaitingUserService _awaitingService;
        private readonly ICommonService _commonService;

        public AwaitingUserController(IAwaitingUserService awaitingService,
                                       ICommonService commonService)
        {
            _awaitingService = awaitingService;
            _commonService = commonService;
        }

        [HttpPost]
        public async Task<ActionResult<AwaitingUser>> Create([FromBody] AwaitingUserApiResource model) {

            if (ModelState.IsValid)
            {
                try {
                    var result = await _awaitingService.CreateUserAsync(model);
                    return result;
                } catch (Exception e)
                {
                    Console.Write(e.Message);
                    throw new ApplicationException();
                }
            }

            throw new ApplicationException("Invalid Model");
        }

        [HttpGet("{Id}", Name = "GetAWAUserDto")]
        public async Task<ActionResult<AwaitingUserApiResource>> Get(int Id)
        {
            var awaitingUser = await _awaitingService.GetUserByIdAsync(Id);

            if (awaitingUser != null)
            {
                return awaitingUser;
            }

            throw new ApplicationException("Awaiting User Not Found");
        }

        [HttpGet(Name = "GetAllAWAUserDto")]
        public async Task<ActionResult<List<AwaitingUserApiResource>>> Get()
        {
            var awaitingUsers = await _awaitingService.GetAwaitingUsers();


            if (awaitingUsers != null)
            {
                return awaitingUsers;
            }

            return NoContent();
            //throw new ApplicationException("Error occured while fetching data");
        }

        [HttpGet("{status}")]
        public async Task<ActionResult<List<AwaitingUserApiResource>>> Status(string status) {
            string Status = _commonService.Capitalize(status) ?? null;
            if (Status == null)
                throw new ApplicationException("Status must not be null");
            else if (!Enum.IsDefined(typeof(EAwaitingUserStatus), Status))
                throw new ApplicationException("Invalid Status");
            var results = await _awaitingService.GetAwaitingUsersByStatus(Status);

            if (results == null)
                return null;

            if (results != null) {
                return results;
            }

            throw new ApplicationException("Error occured while fetching data");
        }

        [HttpPut(Name = "UpdateUser")]
        public async Task<ActionResult<AwaitingUser>> Update([FromBody] AwaitingUser awaitingUser)
        {
            if(ModelState.IsValid)
            {
                var result = await _awaitingService.UpdateAwaitingUser(awaitingUser);

                if(result != null)
                {
                    return result;
                }
            }

            throw new ApplicationException("Invalid Request");
        }

        [HttpDelete("{Id}",Name = "DeleteUser")]
        public async Task<AwaitingUser> Delete(int Id)
        {
            var result = await _awaitingService.DeleteAwaitingUser(Id);

            if (result != null)
            {
                return result;
            }

            throw new ApplicationException("Delete User Failed");
        }

        [HttpPost]
        [Authorize]
        public string Approve()
        {
            return "OK";
        }
    }
}