using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace TheJukeBox.API
{
    [ApiController]
    [Route("/")]
    public class HomeController : ControllerBase
    {
        [HttpGet]
        public IActionResult Home()
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "CLIENT/HTML", "index.html");
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("Homepage not found");
            }

            return PhysicalFile(filePath, "text/html");
        }
    }
}