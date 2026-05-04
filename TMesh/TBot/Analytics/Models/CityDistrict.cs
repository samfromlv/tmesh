using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Analytics.Models
{
    public class CityDistrict
    {
        public int Id { get; set; }

        public string Name { get; set; }

        public int CityId { get; set; }

        public Geometry Borders { get; set; }
    }
}
