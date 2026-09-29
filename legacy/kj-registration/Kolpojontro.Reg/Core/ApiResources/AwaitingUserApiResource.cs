using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.ApiResources
{
    public class AwaitingUserApiResource
    {
        public int Id { get; set; } 
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Gender { get; set; }
        public string ReasonForJoining { get; set; }
        public string PresentOrganization { get; set; }
        public string VolunteeingExperience { get; set; }
        public string DateOfBirth { get; set; }
        public string CityOfResidence { get; set; }
        public string CountryOfResidence { get; set; }
    }
}
