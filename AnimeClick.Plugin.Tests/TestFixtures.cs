internal static class TestFixtures
{
    public const string DangersEpisodesHtml = """
<html><body>
<table class="table"><tbody>
<tr><td>S1 Ep. 01</td><td><a href="/episodio/90001/s1-1">S1 titolo 1</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 02</td><td><a href="/episodio/90002/s1-2">S1 titolo 2</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 03</td><td><a href="/episodio/90003/s1-3">S1 titolo 3</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 04</td><td><a href="/episodio/90004/s1-4">S1 titolo 4</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 05</td><td><a href="/episodio/90005/s1-5">S1 titolo 5</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 06</td><td><a href="/episodio/90006/s1-6">S1 titolo 6</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 07</td><td><a href="/episodio/90007/s1-7">S1 titolo 7</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 08</td><td><a href="/episodio/90008/s1-8">S1 titolo 8</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 09</td><td><a href="/episodio/90009/s1-9">S1 titolo 9</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 10</td><td><a href="/episodio/90010/s1-10">S1 titolo 10</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 11</td><td><a href="/episodio/90011/s1-11">S1 titolo 11</a></td><td>23'</td></tr>
<tr><td>S1 Ep. 12</td><td><a href="/episodio/90012/s1-12">S1 titolo 12</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 13</td><td><a href="/episodio/90013/noi-stiamo-cercando">Noi stiamo cercando</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 14</td><td><a href="/episodio/90014/s2-2">S2 titolo 2</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 15</td><td><a href="/episodio/90015/s2-3">S2 titolo 3</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 16</td><td><a href="/episodio/90016/s2-4">S2 titolo 4</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 17</td><td><a href="/episodio/90017/io-voglio-saperne-di-piu">Io voglio saperne di piu</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 18</td><td><a href="/episodio/90018/s2-6">S2 titolo 6</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 19</td><td><a href="/episodio/90019/s2-7">S2 titolo 7</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 20</td><td><a href="/episodio/90020/s2-8">S2 titolo 8</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 21</td><td><a href="/episodio/90021/s2-9">S2 titolo 9</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 22</td><td><a href="/episodio/90022/s2-10">S2 titolo 10</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 23</td><td><a href="/episodio/90023/s2-11">S2 titolo 11</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 24</td><td><a href="/episodio/90024/s2-12">S2 titolo 12</a></td><td>23'</td></tr>
<tr><td>S2 Ep. 25</td><td><a href="/episodio/90025/il-nostro-amore-piu-puro">Il nostro amore piu puro</a></td><td>23'</td></tr>
</tbody></table>
</body></html>
""";

    public const string SearchHtml = """
<html><body>
<div class="media item-search-item">
  <h4 class="media-heading"><a href="/anime/70000/the-dangers-movie">The Dangers in My Heart</a></h4>
  <ul><li>categoria: Movie</li><li>anno inizio: 2026</li></ul>
</div>
<div class="media item-search-item">
  <h4 class="media-heading"><a href="/anime/44780/boku-no-kokoro-no-yabai-yatsu">The Dangers in My Heart</a></h4>
  <ul><li>categoria: Serie TV</li><li>anno inizio: 2023</li></ul>
</div>
<div class="media item-search-item">
  <h4 class="media-heading"><a href="/anime/70001/the-dangers-special">The Dangers in My Heart Special</a></h4>
  <ul><li>categoria: Special</li><li>anno inizio: 2024</li></ul>
</div>
</body></html>
""";

    public const string TrailerOnlyMultimediaHtml = """
<html><body>
<h3>Trailer</h3>
<iframe src="https://www.youtube.com/embed/example"></iframe>
<h3>PV 1</h3>
</body></html>
""";

    /// <summary>
    /// Mimics AnimeClick's real /staff page for "You and I are Polar Opposites": the sigle are
    /// role sections like any other credit, and its /multimedia page carries no OP/ED block at
    /// all, so this page is the only place they appear.
    /// </summary>
    public const string StaffWithThemeSongsHtml = """
<html><body>
<h4>Regia</h4>
<div class="well">
  <div class="media">
    <h4 class="media-heading"><a href="/autore/1/takakazu-nagatomo">Takakazu Nagatomo</a></h4>
  </div>
</div>
<h4>Character Design</h4>
<div class="well">
  <div class="media">
    <h4 class="media-heading"><a href="/autore/2/mako-miyako">Mako Miyako</a></h4>
  </div>
</div>
<h4>Opening - Megane o hazushite</h4>
<div class="well">
  <div class="media">
    <h4 class="media-heading"><a href="/autore/3/noa">Noa</a></h4>
  </div>
</div>
<h4>Ending - Pure</h4>
<div class="well">
  <div class="media">
    <h4 class="media-heading"><a href="/autore/4/eriko-hashimoto">Eriko Hashimoto</a></h4>
  </div>
  <div class="media">
    <h4 class="media-heading"><a href="/autore/5/pas-tasta">PAS TASTA</a></h4>
  </div>
</div>
<h4>Opening 2 - nekojarashi</h4>
<div class="well">
  <div class="media">
    <h4 class="media-heading"><a href="/autore/6/7co">7co</a></h4>
  </div>
</div>
</body></html>
""";

    /// <summary>
    /// Mimics AnimeClick's real /episodi page for "The Asterisk War" (24 episodes listed
    /// as a continuous block with NO "S1/S2 Ep." row prefixes — only "Ep. NN").
    /// The companion detail page declares 2 stagioni (Autunno 2015 / Primavera 2016),
    /// so the caller passes seasonsCount=2 and the parser synthesises SeasonNumber.
    /// </summary>
    public const string AsteriskContinuousEpisodesHtml = """
<html><body>
<table class="table"><tbody>
<tr><td>Ep. 01</td><td><a href="/episodio/194633/la-strega-della-fiamma-splendente">La strega della fiamma splendente</a></td><td>23'</td></tr>
<tr><td>Ep. 02</td><td><a href="/episodio/194634/ser-veresta">Ser Veresta</a></td><td>23'</td></tr>
<tr><td>Ep. 03</td><td><a href="/episodio/194635/una-vacanza-per-due">Una vacanza per due</a></td><td>23'</td></tr>
<tr><td>Ep. 04</td><td><a href="/episodio/194636/la-liberazione">La liberazione</a></td><td>23'</td></tr>
<tr><td>Ep. 05</td><td><a href="/episodio/194637/la-lama-del-rombo-di-tuono">La lama del rombo di tuono</a></td><td>23'</td></tr>
<tr><td>Ep. 06</td><td><a href="/episodio/194638/il-vero-volto-della-ragazza">Il vero volto della ragazza</a></td><td>23'</td></tr>
<tr><td>Ep. 07</td><td><a href="/episodio/194639/decisioni-e-duelli">Decisioni e duelli</a></td><td>23'</td></tr>
<tr><td>Ep. 08</td><td><a href="/episodio/194640/una-vacanza-per-due-2">Una vacanza per due ②</a></td><td>23'</td></tr>
<tr><td>Ep. 09</td><td><a href="/episodio/194641/la-phoenix-festa">La Phoenix Festa</a></td><td>23'</td></tr>
<tr><td>Ep. 10</td><td><a href="/episodio/194642/la-tirannica-principessa-vampiro">La tirannica principessa vampiro</a></td><td>23'</td></tr>
<tr><td>Ep. 11</td><td><a href="/episodio/194643/il-potere-ed-il-suo-prezzo">Il potere ed il suo prezzo</a></td><td>23'</td></tr>
<tr><td>Ep. 12</td><td><a href="/episodio/196877/la-gravi-sheath">La Gravi-Sheath</a></td><td>23'</td></tr>
<tr><td>Ep. 13</td><td><a href="/episodio/211462/banyuu-tenra">Banyuu Tenra - Rivelazioni divine</a></td><td>23'</td></tr>
<tr><td>Ep. 14</td><td><a href="/episodio/211474/il-re-del-male">Il re del male</a></td><td>23'</td></tr>
<tr><td>Ep. 15</td><td><a href="/episodio/211475/infrangere-la-barriera-dei-ricordi">Infrangere la barriera dei ricordi</a></td><td>23'</td></tr>
<tr><td>Ep. 16</td><td><a href="/episodio/211476/mai-arrendersi">Mai arrendersi</a></td><td>23'</td></tr>
<tr><td>Ep. 17</td><td><a href="/episodio/211477/la-rete-di-fili-di-tyrant">La rete di fili di Tyrant</a></td><td>23'</td></tr>
<tr><td>Ep. 18</td><td><a href="/episodio/211478/sforzi">Sforzi</a></td><td>23'</td></tr>
<tr><td>Ep. 19</td><td><a href="/episodio/211479/melodia">Melodia</a></td><td>23'</td></tr>
<tr><td>Ep. 20</td><td><a href="/episodio/211480/scontro-finale">Scontro finale</a></td><td>23'</td></tr>
<tr><td>Ep. 21</td><td><a href="/episodio/211481/conclusione">Conclusione</a></td><td>23'</td></tr>
<tr><td>Ep. 22</td><td><a href="/episodio/211482/lieseltania">Lieseltania</a></td><td>23'</td></tr>
<tr><td>Ep. 23</td><td><a href="/episodio/211483/erenshkigal">Erenshkigal</a></td><td>23'</td></tr>
<tr><td>Ep. 24</td><td><a href="/episodio/211484/riunione">Riunione</a></td><td>23'</td></tr>
</tbody></table>
</body></html>
""";

    /// <summary>
    /// Same episodes as <see cref="AsteriskContinuousEpisodesHtml"/> but the AnimeClick detail
    /// page declares only ONE season (a 24-ep single cour) — so the parser must NOT split.
    /// </summary>
    public const string SingleCour24EpisodesHtml = """
<html><body>
<table class="table"><tbody>
<tr><td>Ep. 01</td><td><a href="/episodio/1/a">A</a></td><td>23'</td></tr>
<tr><td>Ep. 24</td><td><a href="/episodio/24/z">Z</a></td><td>23'</td></tr>
</tbody></table>
</body></html>
""";
    /// <summary>
    /// Builds a flat /episodi table of <paramref name="count"/> rows, every one declaring the
    /// same duration. Used to reproduce a short-form broadcast documented on AnimeClick against
    /// a library that holds the full length recut of the same episodes.
    /// </summary>
    public static string BuildFlatEpisodesHtml(int count, int durationMinutes, bool realTitles)
    {
        var rows = new System.Text.StringBuilder();
        for (var number = 1; number <= count; number++)
        {
            var title = realTitles
                ? System.FormattableString.Invariant($"Titolo vero {number}")
                : System.FormattableString.Invariant($"Episodio {number:00}");
            rows.Append(System.FormattableString.Invariant(
                $"<tr><td>Ep. {number:00}</td><td><a href=\"/episodio/{9000 + number}/riga-{number}\">{title}</a></td><td>{durationMinutes}'</td></tr>\n"));
        }

        return "<html><body>\n<table class=\"table\"><tbody>\n" + rows + "</tbody></table>\n</body></html>";
    }

    /// <summary>
    /// A season that opens with a prologue numbered zero, the way AnimeClick prints it: the row
    /// sits in the same table as the regular episodes but its number is not positive, so the
    /// parser files it among the specials.
    /// </summary>
    public const string EpisodeZeroPrologueHtml = """
<html><body>
<table class="table"><tbody>
<tr><td>Ep. 00</td><td><a href="/episodio/8000/prologo">Prologo</a></td><td>24'</td></tr>
<tr><td>Ep. 01</td><td><a href="/episodio/8001/il-primo-giorno">Il primo giorno</a></td><td>24'</td></tr>
<tr><td>Ep. 02</td><td><a href="/episodio/8002/il-secondo-giorno">Il secondo giorno</a></td><td>24'</td></tr>
<tr><td>Ep. 03</td><td><a href="/episodio/8003/il-terzo-giorno">Il terzo giorno</a></td><td>24'</td></tr>
</tbody></table>
</body></html>
""";

    /// <summary>
    /// Two characters voiced by the same seiyuu, the shape that costs a credit on save: Jellyfin
    /// keeps one row per name and kind, so the second character has to be merged into the first
    /// credit or it disappears.
    /// </summary>
    public const string DoubleRoleCharactersHtml = """
<html><body>
<div class="media thumbnail thumbnail-personaggio">
  <span itemprop="character"><span itemprop="name">Rikako Honda</span></span>
  <span itemprop="actor"><a itemprop="url" href="/autore/100/tomori-kusunoki"></a><span itemprop="name">Tomori Kusunoki</span></span>
</div>
<div class="media thumbnail thumbnail-personaggio">
  <span itemprop="character"><span itemprop="name">Yeti</span></span>
  <span itemprop="actor"><a itemprop="url" href="/autore/100/tomori-kusunoki"></a><span itemprop="name">Tomori Kusunoki</span></span>
</div>
<div class="media thumbnail thumbnail-personaggio">
  <span itemprop="character"><span itemprop="name">Miyu Suzuki</span></span>
  <span itemprop="actor"><a itemprop="url" href="/autore/101/sayumi-suzushiro"></a><span itemprop="name">Sayumi Suzushiro</span></span>
</div>
</body></html>
""";

    /// <summary>
    /// A card that lists a numbered spin-off inside the episode table: K-On!!'s own episodes and
    /// then the "Ura-On!!" shorts, whose numbers collide with the first ones.
    /// </summary>
    public const string SpinOffInsideTableHtml = """
<html><body>
<table class="table"><tbody>
<tr><td>Ep. 01</td><td><a href="/episodio/7001/terzo-anno">Terzo Anno!</a></td><td>24'</td></tr>
<tr><td>Ep. 02</td><td><a href="/episodio/7002/pulizie">Pulizie!</a></td><td>24'</td></tr>
<tr><td>Ep. 03</td><td><a href="/episodio/7003/batterista">Batterista!</a></td><td>24'</td></tr>
<tr><td>Ep. 04</td><td><a href="/episodio/7004/gita">Gita scolastica!</a></td><td>24'</td></tr>
<tr><td>Ep. 25 (extra)</td><td><a href="/episodio/7025/pianificazione">Pianificazione!</a></td><td>24'</td></tr>
<tr><td>Ura-On!! 01</td><td><a href="/episodio/7101/destino">Lettura del destino per tutti</a></td><td>3'</td></tr>
<tr><td>Ura-On!! 02</td><td><a href="/episodio/7102/souvenir">Storie di Souvenir</a></td><td>3'</td></tr>
<tr><td>Ura-On!! 03</td><td><a href="/episodio/7103/fratellino">Voglio un fratellino!</a></td><td>3'</td></tr>
</tbody></table>
</body></html>
""";
}
