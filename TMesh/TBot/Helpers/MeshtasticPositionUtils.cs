using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TBot.Helpers
{
    public static class MeshtasticPositionUtils
    {
        // Earth's circumference at the equator in meters
        private const double EarthCircumference = 40075016.0;

        /// <summary>
        /// Calculates approximate location accuracy (in meters) from precision bits (Meshtastic packets).
        /// </summary>
        public static double PrecisionBitsToAccuracyMeters(int precisionBits)
        {
            if (precisionBits == 0)
                return 0; //full accuracy

            if (precisionBits < 1 || precisionBits > 32)
                throw new ArgumentOutOfRangeException(nameof(precisionBits), "precisionBits must be between 1 and 32.");

            int possibleValues = 1 << precisionBits;
            double degreeStep = 180.0 / possibleValues;
            double metersPerDegree = EarthCircumference / 360.0;
            double accuracyMeters = (degreeStep * metersPerDegree) / 2.0;
            return accuracyMeters;
        }

        /// <summary>
        /// Converts the ground_track value (tenths of degree) to heading [0, 360) degrees.
        /// </summary>
        public static int GroundTrackToHeading(int groundTrack)
        {
            double heading = groundTrack / 10.0;
            heading = ((heading % 360) + 360) % 360;
            return (int)Math.Round(heading);
        }

        /// <summary>
        /// Calculates accuracy in meters using HDOP or PDOP. 
        /// Standard GPS formula: DOP × User Equivalent Range Error (UERE), with typical UERE ≈ 6m outdoors.
        /// </summary>
        /// <param name="dop">HDOP or PDOP value (horizontal or positional dilution of precision)</param>
        /// <param name="uereMeters">User Equivalent Range Error, defaults to 6 meters if not specified.</param>
        /// <returns>Estimated accuracy in meters.</returns>
        public static double DopToAccuracyMeters(double dop, double uereMeters = 6.0)
        {
            if (dop < 0) throw new ArgumentOutOfRangeException(nameof(dop), "DOP must not be negative.");
            return dop * uereMeters;
        }

        /// <summary>
        /// Returns the greatest (least accurate) value among a set of provided accuracy measurements.
        /// </summary>
        public static double WorstAccuracy(params double[] accuracies)
        {
            double worst = 0;
            foreach (var acc in accuracies)
                if (acc > worst) worst = acc;
            return worst;
        }

        public static int DistanceMetersRound(
               (double lat, double lon) from,
               (double lat, double lon) to) => (int) Math.Round(DistanceMeters(from, to));


        public static double DistanceMeters(
                (double lat, double lon) from,
                (double lat, double lon) to) => DistanceMeters(from.lat, from.lon, to.lat, to.lon);



        public static double DistanceMeters(
                double lat1, double lon1,
                double lat2, double lon2)
        {
            const double earthRadiusMeters = 6371008.8; // mean Earth radius

            static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

            double phi1 = ToRadians(lat1);
            double phi2 = ToRadians(lat2);
            double dPhi = ToRadians(lat2 - lat1);
            double dLambda = ToRadians(lon2 - lon1);

            double a =
                Math.Sin(dPhi / 2) * Math.Sin(dPhi / 2) +
                Math.Cos(phi1) * Math.Cos(phi2) *
                Math.Sin(dLambda / 2) * Math.Sin(dLambda / 2);

            double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

            return earthRadiusMeters * c;
        }
    }
}
