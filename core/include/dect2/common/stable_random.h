// std::normal_distribution and std::uniform_real_distribution that give the same numbers on every platform.
//
// The standard fixes the engines (std::mt19937 is the same everywhere) but not the distributions: libc++ (macOS) and libstdc++ (Linux, MinGW) turn the
// same engine output into different normal numbers (libstdc++ returns the second value of a pair first, and both differ in rounding details). The
// test signal generators and the tests were tuned with libc++, so on the other platforms the same seed gave another noise realisation, and the
// marginal ones (a lock at -10 dB echo, a beacon at 9 dB) failed there. These classes follow libc++'s algorithm step by step (float / double
// arithmetic included), so macOS keeps its numbers and Linux and Windows now get the same ones. Only the draws are shared, math functions are the
// platform's (logf / log differ in the last bit between libms, which does not matter for noise).
#pragma once
#include <cmath>
#include <cstdint>
#include <limits>

namespace dect2 {
namespace stable_random_detail {
// libc++ generate_canonical<Real, digits>(engine): `k` draws of the engine, scaled to [0, 1)
template <class Real, class Engine> Real canonical(Engine& g) {
    const int bits = std::numeric_limits<Real>::digits;
    // log2 of the engine's range (max - min + 1); a 64-bit engine has 2^64 values
    const uint64_t span = (uint64_t)(Engine::max() - Engine::min());
    int logR = 0;
    if (span == ~uint64_t(0)) logR = 64;
    else { uint64_t v = span + 1; while (v > 1) { v >>= 1; logR++; } }
    const int b = bits;
    const int k = b / logR + (b % logR != 0) + (b == 0);
    const Real rp = (Real)(std::ldexp(1.0L, logR));
    Real base = rp;
    Real sp = (Real)(g() - Engine::min());
    for (int i = 1; i < k; i++, base *= rp) sp += (Real)(g() - Engine::min()) * base;
    Real s = sp / base;
    if (s >= 1) s = std::nextafter((Real)1, (Real)0);
    return s;
}
}  // namespace stable_random_detail

template <class Real = double> class uniform_real_distribution {
public:
    using result_type = Real;
    explicit uniform_real_distribution(Real a = 0, Real b = 1) : a_(a), b_(b) {}
    template <class Engine> Real operator()(Engine& g) { return (b_ - a_) * stable_random_detail::canonical<Real>(g) + a_; }
private:
    Real a_, b_;
};

template <class Real = double> class normal_distribution {
public:
    using result_type = Real;
    explicit normal_distribution(Real mean = 0, Real stddev = 1) : mean_(mean), stddev_(stddev) {}
    template <class Engine> Real operator()(Engine& g) {
        Real r;
        if (hot_) {
            hot_ = false;
            r = saved_;
        } else {
            uniform_real_distribution<Real> uni(-1, 1);
            Real u, v, s;
            do {
                u = uni(g);
                v = uni(g);
                s = u * u + v * v;
            } while (s > 1 || s == 0);
            const Real fp = std::sqrt(-2 * std::log(s) / s);
            saved_ = v * fp;
            hot_ = true;
            r = u * fp;
        }
        return r * stddev_ + mean_;
    }
private:
    Real mean_, stddev_, saved_ = 0;
    bool hot_ = false;
};
}  // namespace dect2
