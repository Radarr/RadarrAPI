﻿using System.Collections.Generic;
using Newtonsoft.Json;

namespace RadarrAPI.Services.ReleaseCheck.AppVeyor.Responses
{
    public class AppVeyorProjectHistory
    {
        
        [JsonProperty("builds")]
        public List<AppVeyorProjectBuild> Builds { get; set; }

    }
}
