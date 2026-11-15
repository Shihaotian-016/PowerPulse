using System;

namespace PowerPulse
{
    // Intel RAPL PP0 包功率: 通过 Ring0 读能量计数器 MSR, 差分求瓦。
    // MSR_RAPL_POWER_UNIT(0x606) bits[8:12] = 能量单位指数 E, 每单位 1/2^E 焦耳(典型 61uJ)。
    // MSR_RAPL_PP0_ENERGY(0x611) 低 17 位为能量计数, 溢出回绕。
    internal sealed class Rapl
    {
        private const uint MSR_RAPL_POWER_UNIT = 0x606;
        private const uint MSR_RAPL_PP0_ENERGY = 0x611;
        private const ulong ENERGY_MASK = 0x1FFFFUL;
        private const double MAX_PLAUSIBLE_WATTS = 1000.0;

        private readonly Ring0 _ring0;

        private bool _available;

        private double _energyUnitJoules;

        private bool _hasLast;

        private double _lastEnergy;

        private DateTime _lastTimeUtc;

        private double _lastWatts = -1.0;

        public bool Available { get { return _available; } }

        public Rapl(Ring0 ring0)
        {
            _ring0 = ring0;
            try
            {
                ulong v;
                if (_ring0.ReadMsr(MSR_RAPL_POWER_UNIT, out v))
                {
                    int unit = (int)((v >> 8) & 0x1FUL);
                    if (unit >= 1 && unit <= 24)
                    {
                        _energyUnitJoules = 1.0 / Math.Pow(2.0, (double)unit);
                        _available = true;
                    }
                }
            }
            catch
            {
                _available = false;
            }
        }

        public void Reset()
        {
            _hasLast = false;
            _lastEnergy = 0.0;
            _lastWatts = -1.0;
        }

        // 返回 PP0 包功率(瓦); 首次调用或不可用返回 -1。
        public double ReadPackageWatts()
        {
            if (!_available)
            {
                return -1.0;
            }
            ulong v;
            if (!_ring0.ReadMsr(MSR_RAPL_PP0_ENERGY, out v))
            {
                return -1.0;
            }
            double cur = (double)(v & ENERGY_MASK) * _energyUnitJoules;
            DateTime now = DateTime.UtcNow;
            if (!_hasLast)
            {
                _hasLast = true;
                _lastEnergy = cur;
                _lastTimeUtc = now;
                return -1.0;
            }
            double dt = (now - _lastTimeUtc).TotalSeconds;
            if (dt < 0.5)
            {
                // 间隔过短, 差分噪声大; 保留基线, 返回上次值
                return _lastWatts;
            }
            double de = cur - _lastEnergy;
            _lastEnergy = cur;
            _lastTimeUtc = now;
            if (de < 0.0)
            {
                // 17 位计数器回绕: 重建基线, 本次跳过
                return _lastWatts;
            }
            double w = de / dt;
            if (w < 0.0 || w > MAX_PLAUSIBLE_WATTS)
            {
                return _lastWatts;
            }
            _lastWatts = w;
            return w;
        }
    }
}
