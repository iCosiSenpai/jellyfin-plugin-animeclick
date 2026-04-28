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
}
