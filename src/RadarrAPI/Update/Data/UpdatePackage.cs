﻿using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace RadarrAPI.Update.Data
{
    public class UpdatePackage
    {

        [JsonConverter(typeof(VersionConverter))]
        public Version Version { get; set; }

        [JsonConverter(typeof(IsoDateTimeConverter))]
        public DateTime ReleaseDate { get; set; }

        public string Filename { get; set; }

        public string Url { get; set; }

        public UpdateChanges Changes { get; set; }

        /// <summary>
        ///     Must be a SHA256 hash of the zip file of <see cref="Url"/>.
        /// </summary>
        public string Hash { get; set; }

        public string Branch { get; set; }

    }
}