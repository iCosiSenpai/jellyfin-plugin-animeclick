using System;
using AnimeClick.Plugin.Services;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AnimeClick.Plugin.Tests;

/// <summary>
/// L'interruttore che smette di chiamare un servizio esterno quando è spento.
///
/// Nasce da AniList, che nel 2026 ha disattivato la propria API: risponde 403 a chiunque,
/// con «The AniList API has been temporarily disabled due to severe stability issues».
/// Il plugin ci riprovava a ogni elemento della libreria, due volte per elemento, riempiendo
/// i log di avvisi identici e pagando ogni volta il ritardo fra le richieste.
/// </summary>
public class AnimeClickCircuitBreakerTests
{
    private static readonly TimeSpan Attesa = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AttesaMassima = TimeSpan.FromMinutes(60);

    private static (AnimeClickCircuitBreaker Breaker, FakeTimeProvider Time) Nuovo(int soglia = 3)
    {
        var time = new FakeTimeProvider();
        return (new AnimeClickCircuitBreaker("prova", soglia, Attesa, AttesaMassima, time), time);
    }

    [Fact]
    public void ChiusoInPartenza_LasciaPassare()
    {
        var (breaker, _) = Nuovo();
        Assert.True(breaker.TryEnter());
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void SottoLaSoglia_LasciaAncoraPassare()
    {
        // Un errore isolato è la rete, non un servizio spento: non deve chiudere nulla.
        var (breaker, _) = Nuovo(soglia: 3);
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void RaggiuntaLaSoglia_SiApreEBlocca()
    {
        var (breaker, _) = Nuovo(soglia: 3);
        for (var i = 0; i < 3; i++) breaker.RecordFailure();

        Assert.True(breaker.IsOpen);
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void UnSuccessoAzzeraIlConteggio()
    {
        var (breaker, _) = Nuovo(soglia: 3);
        breaker.RecordFailure();
        breaker.RecordFailure();
        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void PassataLAttesa_ConcedeUnSoloTentativo()
    {
        var (breaker, time) = Nuovo(soglia: 2);
        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.False(breaker.TryEnter());

        time.Advance(Attesa + TimeSpan.FromSeconds(1));

        // Uno passa, per capire se il servizio è tornato…
        Assert.True(breaker.TryEnter());
        // …ma solo uno: finché quello non risponde, gli altri restano fuori.
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void IlTentativoRiuscito_RiapreDavvero()
    {
        var (breaker, time) = Nuovo(soglia: 2);
        breaker.RecordFailure();
        breaker.RecordFailure();
        time.Advance(Attesa + TimeSpan.FromSeconds(1));

        Assert.True(breaker.TryEnter());
        breaker.RecordSuccess();

        Assert.False(breaker.IsOpen);
        Assert.True(breaker.TryEnter());
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void IlTentativoFallito_RichiudeSubito()
    {
        var (breaker, time) = Nuovo(soglia: 2);
        breaker.RecordFailure();
        breaker.RecordFailure();
        time.Advance(Attesa + TimeSpan.FromSeconds(1));

        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();

        Assert.True(breaker.IsOpen);
        Assert.False(breaker.TryEnter());
    }

    [Fact]
    public void AttesaRaddoppiaAOgniRicaduta()
    {
        // Un servizio spento da settimane non merita un tentativo ogni cinque minuti.
        var (breaker, time) = Nuovo(soglia: 1);
        breaker.RecordFailure();                       // apertura 1: attesa 5 min

        time.Advance(Attesa + TimeSpan.FromSeconds(1));
        Assert.True(breaker.TryEnter());
        breaker.RecordFailure();                       // apertura 2: attesa 10 min

        time.Advance(Attesa + TimeSpan.FromSeconds(1)); // 5 min non bastano più
        Assert.False(breaker.TryEnter());

        time.Advance(Attesa);                           // arrivati a 10 min
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void LAttesaNonSuperaIlTetto()
    {
        var (breaker, time) = Nuovo(soglia: 1);
        for (var i = 0; i < 20; i++)
        {
            breaker.RecordFailure();
            time.Advance(AttesaMassima + TimeSpan.FromSeconds(1));
            breaker.TryEnter();
        }

        breaker.RecordFailure();
        time.Advance(AttesaMassima + TimeSpan.FromSeconds(1));
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void SenzaVerdetto_NonContaComeFallimento()
    {
        // Un 429 dice «più piano», non «sono giù»: ha già la sua pausa altrove e non deve
        // avvicinare l'apertura dell'interruttore.
        var (breaker, _) = Nuovo(soglia: 2);
        breaker.RecordFailure();
        breaker.RecordIndeterminate();
        breaker.RecordIndeterminate();

        Assert.False(breaker.IsOpen);
        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public void SenzaVerdetto_RestituisceIlTentativoDiProva()
    {
        // Se il tentativo di prova finisce senza risposta utile — annullamento, rate limit —
        // va restituito, altrimenti resta appeso e blocca ogni chiamata per sempre.
        var (breaker, time) = Nuovo(soglia: 1);
        breaker.RecordFailure();
        time.Advance(Attesa + TimeSpan.FromSeconds(1));

        Assert.True(breaker.TryEnter());
        Assert.False(breaker.TryEnter());   // il tentativo è in corso
        breaker.RecordIndeterminate();

        Assert.True(breaker.TryEnter());    // restituito: si può riprovare
    }

    [Fact]
    public void SenzaVerdetto_ConLInterruttoreChiusoNonCambiaNulla()
    {
        var (breaker, _) = Nuovo(soglia: 2);
        breaker.RecordIndeterminate();

        Assert.False(breaker.IsOpen);
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void DiceQuandoSiApre_UnaVoltaSola()
    {
        // Serve a scrivere un avviso nel log all'apertura e non a ogni chiamata bloccata:
        // è il rumore che ha reso questo problema invisibile per giorni.
        var (breaker, _) = Nuovo(soglia: 2);

        Assert.False(breaker.RecordFailure());
        Assert.True(breaker.RecordFailure());   // questa apre
        Assert.False(breaker.RecordFailure());  // già aperto: niente da annunciare
    }
}
