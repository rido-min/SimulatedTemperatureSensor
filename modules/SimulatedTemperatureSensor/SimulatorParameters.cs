using System;
using System.Collections.Generic;
using System.Text;

namespace SimulatedTemperatureSensor
{
    class SimulatorParameters
    {
        public double MachineTempMin { get; set; }
        public double MachineTempMax { get; set; }
        public double MachinePressureMin { get; set; }
        public double MachinePressureMax { get; set; }
        public double AmbientTemp { get; set; }
        public int HumidityPercent { get; set; }
    }
}
