using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace TheJukeBox.API
{
    [ApiController]
    [Route("[controller]")]
    public class PlayerController : ControllerBase
    {
        [HttpGet]
        public IActionResult Player()
        {
            var filePath = Path.Combine(Directory.GetCurrentDirectory(), "CLIENT/HTML", "player.html");
            if (!System.IO.File.Exists(filePath))
            {
                return NotFound("Player page not found");
            }

            return PhysicalFile(filePath, "text/html");
        }
    }
}