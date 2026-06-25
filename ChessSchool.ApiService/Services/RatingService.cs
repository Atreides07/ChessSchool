using ChessSchool.Contracts;

namespace ChessSchool.ApiService.Services;

/// <summary>Состояние рейтинга игрока на входе (рейтинг, отклонение RD, волатильность).</summary>
public readonly record struct PlayerRating(double Rating, double Rd, double Volatility);

/// <summary>Новое состояние рейтинга игрока после партии.</summary>
public readonly record struct RatingUpdate(int Rating, double Rd, double Volatility, int Delta);

/// <summary>
/// Абстракция рейтинговой системы. Реализована Glicko-2 (учитывает неопределённость RD
/// и волатильность). Интерфейс позволяет подменить алгоритм без правок вызывающего кода.
/// </summary>
public interface IRatingService
{
    (RatingUpdate White, RatingUpdate Black) Compute(PlayerRating white, PlayerRating black, GameResult result);
}

/// <summary>
/// Реализация Glicko-2 (Mark Glickman) для обновления по одной партии.
/// Параметры по умолчанию: стартовый рейтинг 1500, RD 350, волатильность 0.06, системная τ = 0.5.
/// </summary>
public sealed class Glicko2RatingService : IRatingService
{
    private const double Scale = 173.7178;   // перевод в шкалу Glicko-2
    private const double Tau = 0.5;          // ограничивает изменение волатильности
    private const double Epsilon = 0.000001;

    public (RatingUpdate White, RatingUpdate Black) Compute(PlayerRating white, PlayerRating black, GameResult result)
    {
        var (sWhite, sBlack) = result switch
        {
            GameResult.WhiteWins => (1.0, 0.0),
            GameResult.BlackWins => (0.0, 1.0),
            _ => (0.5, 0.5)
        };
        return (UpdateOne(white, black, sWhite), UpdateOne(black, white, sBlack));
    }

    private static RatingUpdate UpdateOne(PlayerRating self, PlayerRating opp, double score)
    {
        // Шаг 2: перевод в шкалу Glicko-2.
        double mu = (self.Rating - 1500) / Scale;
        double phi = self.Rd / Scale;
        double sigma = self.Volatility;

        double muOpp = (opp.Rating - 1500) / Scale;
        double phiOpp = opp.Rd / Scale;

        // Шаг 3-4: вспомогательные величины.
        double g = 1.0 / Math.Sqrt(1.0 + 3.0 * phiOpp * phiOpp / (Math.PI * Math.PI));
        double e = 1.0 / (1.0 + Math.Exp(-g * (mu - muOpp)));
        double v = 1.0 / (g * g * e * (1.0 - e));
        double delta = v * g * (score - e);

        // Шаг 5: новая волатильность (итерация Illinois).
        double sigmaPrime = NewVolatility(phi, v, delta, sigma);

        // Шаг 6-7: новое отклонение и рейтинг.
        double phiStar = Math.Sqrt(phi * phi + sigmaPrime * sigmaPrime);
        double phiPrime = 1.0 / Math.Sqrt(1.0 / (phiStar * phiStar) + 1.0 / v);
        double muPrime = mu + phiPrime * phiPrime * g * (score - e);

        // Шаг 8: обратно в шкалу рейтинга.
        int newRating = (int)Math.Round(Scale * muPrime + 1500);
        double newRd = Scale * phiPrime;

        return new RatingUpdate(newRating, newRd, sigmaPrime, newRating - (int)Math.Round(self.Rating));
    }

    private static double NewVolatility(double phi, double v, double delta, double sigma)
    {
        double a = Math.Log(sigma * sigma);
        double delta2 = delta * delta;
        double phi2 = phi * phi;

        double F(double x)
        {
            double ex = Math.Exp(x);
            double num = ex * (delta2 - phi2 - v - ex);
            double den = 2.0 * Math.Pow(phi2 + v + ex, 2);
            return num / den - (x - a) / (Tau * Tau);
        }

        double A = a;
        double B;
        if (delta2 > phi2 + v)
        {
            B = Math.Log(delta2 - phi2 - v);
        }
        else
        {
            int k = 1;
            while (F(a - k * Tau) < 0) k++;
            B = a - k * Tau;
        }

        double fa = F(A), fb = F(B);
        while (Math.Abs(B - A) > Epsilon)
        {
            double c = A + (A - B) * fa / (fb - fa);
            double fc = F(c);
            if (fc * fb <= 0) { A = B; fa = fb; }
            else { fa /= 2.0; }
            B = c; fb = fc;
        }
        return Math.Exp(A / 2.0);
    }
}
