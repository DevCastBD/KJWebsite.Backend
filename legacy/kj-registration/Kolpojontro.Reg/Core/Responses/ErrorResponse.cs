using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Kolpojontro.Reg.Core.Responses
{
    public class ErrorResponse
    {
        //public T Obj { get; set;}
        public int StatusCode { get; set; }
        public string Message { get; set; }

        public ErrorResponse(int statusCode, string message)
        {
            StatusCode = statusCode;
            Message = message;
        }

        public override string ToString()
        {
            return $"Status: {StatusCode}; Message: {Message}";
        }
    }
}
