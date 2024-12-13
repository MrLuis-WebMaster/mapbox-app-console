

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using System.Net;

namespace ConsoleApp1
{
    public static class Program
    {

        private static readonly string JsonFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "json");
        private static readonly string ImagesFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "images");
        private static readonly string _accessToken = "sk.eyJ1IjoibXItbHVpczI3IiwiYSI6ImNtM3h2Z25jazFueTYybnB3NXpoNDNwNmIifQ.V6Z-LmsLQDEoZuMLwTQJhQ";
        private static readonly string _styleIdMapbox = "clzbou9dq00np01qmdyrk5lds";
        private static readonly string _urlMapbox = "https://api.mapbox.com/tilesets/v1/";
        private static readonly string _urlMapboxStyles = "https://api.mapbox.com/styles/v1/";
        private static readonly string _usernameMapbox = "mr-luis27";

        private static SemaphoreSlim _rateLimiter = new SemaphoreSlim(1, 1);
        private static int _requestCount = 0;
        private static DateTime _startTime = DateTime.UtcNow;

        static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("Iniciando la generación de imágenes...");

                Directory.CreateDirectory(ImagesFolderPath);

                var jsonFiles = Directory.GetFiles(JsonFolderPath, "*.json");

                if (!jsonFiles.Any())
                {
                    Console.WriteLine("No se encontraron archivos JSON en la carpeta 'json'.");
                    return;
                }
                foreach (var fileToProcess in jsonFiles)
                {
                    Console.WriteLine($"Procesando archivo: {Path.GetFileName(fileToProcess)}");

                    var jsonData = await File.ReadAllTextAsync(fileToProcess);

                    var data = JsonSerializer.Deserialize<IList<SamplesDetailSaveDto>>(jsonData, new JsonSerializerOptions());

                    await GenerateImage(data);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }




        public static async Task GenerateImage(IList<SamplesDetailSaveDto> data)
        {
            try
            {
                var coordinates = GetLatLongEnumerableYield(data);
                var file = PrepareGeoJson(coordinates);
                var tilesetSourceId = await UploadTilesetSourceGeoJson(file);
                var tilesetId = await CreateTileset(tilesetSourceId, coordinates);
                var jobId = await PublishTileset(tilesetId);
                var isPublish = await GetTilesetStatus(tilesetId, jobId, 0);

                if (isPublish)
                {
                    var center = await GetTilesetCenter(tilesetId);
                    if (center is { Item1: not null, Item2: not null, Item3: not null })
                    {
                        var bbox = CalculateBoundingBox(coordinates);
                        var image = await GetStaticImage(tilesetId, bbox);
                        var fileName = $"{tilesetId}-prueba.jpg";
                        var filePath = Path.Combine(ImagesFolderPath, $"{fileName}");
                        await File.WriteAllBytesAsync(filePath, image);
                        Console.WriteLine($"Imagen guardada en: {filePath}");
                        await DeleteTilesetAsync(tilesetId);
                        await DeleteTilesetSourceAsync(tilesetSourceId);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }


        private static IEnumerable<double[]> GetLatLongEnumerableYield(IList<SamplesDetailSaveDto> data)
        {
            foreach (var item in data)
            {
                if (item is { LongitudeInDegree: 0, LatitudeInDegree: 0 }) continue;
                yield return [item.LongitudeInDegree, item.LatitudeInDegree];
            }
        }

        private static string PrepareGeoJson(IEnumerable<double[]> coordinates)
        {
            var json = new
            {
                type = "Feature",
                geometry = new
                {
                    type = "LineString",
                    coordinates
                }
            };
            return JsonSerializer.Serialize(json);
        }


        private static async Task<string> UploadTilesetSourceGeoJson(string geoJsonContent)
        {
            var id = GenerateRandomId(10);
            var requestUrl = $"{_urlMapbox}sources/{_usernameMapbox}/{id}?access_token={_accessToken}";

            const int maxRequestsPerMinute = 100;
            const int timeFrameInSeconds = 5;

            try
            {
                await ControlRateLimit(maxRequestsPerMinute, timeFrameInSeconds);

                using var client = new HttpClient();
                var fileStream = new MemoryStream(Encoding.UTF8.GetBytes(geoJsonContent));
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
                {
                    Name = "file",
                    FileName = $"{id}.json"
                };
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");

                var content = new MultipartFormDataContent
        {
            { fileContent, "file", $"{id}.json" }
        };
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(requestUrl),
                    Content = content
                };

                using var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(result);
                    return id;
                }
                else
                {
                    var result = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(result);
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error while uploading tileset source GeoJSON: {ex.Message}", ex);
            }
        }



        private static async Task<string> CreateTileset(string sourceId, IEnumerable<double[]> coordinates)
        {
            var tilesetId = GenerateRandomId(10);

            var bbox = CalculateBoundingBox(coordinates);

            var requestUrl = $"{_urlMapbox}{_usernameMapbox}.{tilesetId}?access_token={_accessToken}";
            var jsonBody = JsonSerializer.Serialize(new
            {
                recipe = new
                {
                    version = 1,
                    fillzoom= 7,
                    layers = new
                    {
                        route_source = new
                        {
                            source = $"mapbox://tileset-source/{_usernameMapbox}/{sourceId}",
                            minzoom = 2,
                            maxzoom = 16,
                            features = new
                            {
                                simplification = 0.5,
                                bbox
                            },
                        }
                    },
                },
                name = "Road User",
                description = "Road description"
            });
            const int maxRequestsPerMinute = 100;
            const int timeFrameInSeconds = 5;

            try
            {
                await ControlRateLimit(maxRequestsPerMinute, timeFrameInSeconds);
                using var client = new HttpClient();
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(requestUrl),
                    Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
                };

                var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    return tilesetId;
                }
                else
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error creating: {ex.Message}", ex);
            }
        }

        private static async Task<string> PublishTileset(string tilesetId)
        {
            var requestUrl = $"{_urlMapbox}{_usernameMapbox}.{tilesetId}/publish?access_token={_accessToken}";

            const int maxRequestsPerMinute = 2;
            const int timeFrameInSeconds = 5;

            try
            {
                await ControlRateLimit(maxRequestsPerMinute, timeFrameInSeconds);

                using var client = new HttpClient();
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(requestUrl)
                };

                using var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    var root = jsonDoc.RootElement;
                    var jobId = root.GetProperty("jobId").GetString() ?? "";

                    return jobId == null ? throw new Exception($"Not found jobId") : jobId;
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(responseBody);
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }

            catch (Exception ex)
            {
                throw new Exception($"Error publishing tileset: {ex.Message}", ex);
            }
        }


        private static async Task<bool> GetTilesetStatus(string tilesetId, string jobId, short counter)
        {
            var requestUrl = $"{_urlMapbox}{_usernameMapbox}.{tilesetId}/jobs/{jobId}?access_token={_accessToken}";

            try
            {
                using var client = new HttpClient();
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(requestUrl)
                };

                using var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();

                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    var root = jsonDoc.RootElement;

                    var stage = root.GetProperty("stage").GetString() ?? null;

                    if (stage == null)
                    {
                        throw new Exception("not found property stage");
                    }

                    if (stage == "processing")
                    {
                        await Task.Delay(5000);
                        return await GetTilesetStatus(tilesetId, jobId, 0);
                    }
                    if (stage == "failed")
                    {
                        if (counter > 3) throw new Exception("Failed to publish tileset");
                        jobId = await PublishTileset(tilesetId);
                        return await GetTilesetStatus(tilesetId, jobId, ++counter);
                    }
                    return stage == "success";

                }
                else
                {
                    Console.WriteLine(response.Content.ReadAsStringAsync());
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting status of tileset: {ex.Message}", ex);
            }
        }


        private static async Task<(double?, double?, int? zoom)> GetTilesetCenter(string tilesetId)
        {
            var requestUrl = $"{_urlMapbox}{_usernameMapbox}?access_token={_accessToken}";

            try
            {
                using var client = new HttpClient();
                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Get,
                    RequestUri = new Uri(requestUrl)
                };

                using var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync();

                    using var jsonDoc = JsonDocument.Parse(responseBody);
                    var tilesets = jsonDoc.RootElement.EnumerateArray();

                    foreach (var tileset in tilesets)
                    {
                        if (tileset.GetProperty("id").GetString() != $"{_usernameMapbox}.{tilesetId}") continue;

                        var centerArray = tileset.GetProperty("center").EnumerateArray().ToList();
                        double? centerX = centerArray.Count > 0 ? centerArray[0].GetDouble() : null;
                        double? centerY = centerArray.Count > 1 ? centerArray[1].GetDouble() : null;
                        int? zoom = centerArray.Count > 2 ? centerArray[2].GetInt32() : null;

                        return (centerX, centerY, zoom);
                    }
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine(responseBody);
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }

            catch (Exception ex)
            {
                throw new Exception($"Error getting center tileset: {ex.Message}", ex);
            }
            return (null, null, null);
        }

        private static (double centerX, double centerY, int zoom) CalculateCenterAndZoom(double[] bbox)
        {
            double centerX = (bbox[0] + bbox[2]) / 2;
            double centerY = (bbox[1] + bbox[3]) / 2; 

            double lngDiff = bbox[2] - bbox[0];
            double latDiff = bbox[3] - bbox[1];
            int zoom = Math.Max(2, Math.Min(16, (int)Math.Floor(16 - Math.Log2(Math.Max(lngDiff, latDiff)))));

            return (centerX, centerY, zoom);
        }


        private static async Task<byte[]> GetStaticImage(string tilesetId, double[] bbox)
        {
            const int width = 1280;
            const int height = 1280;
            var padding = 50;


            try
            {
                var layer = new Dictionary<string, object>
        {
            { "id", "route-layer" },
            { "type", "line" },
            {
                "source", new Dictionary<string, object>
                {
                    { "type", "vector" },
                    { "url", $"mapbox://{_usernameMapbox}.{tilesetId}" }
                }
            },
            { "source-layer", "route_source" },
            {
                "paint", new Dictionary<string, object>
                {
                    { "line-color", "#a3e635" },
                    { "line-width", 4 },
                    { "line-join", "round" },
                    { "line-cap", "round" }
                }
            }
        };

                var layerJson = JsonSerializer.Serialize(layer);
                var encodedLayer = Uri.EscapeDataString(layerJson);
                var bboxString = $"[{string.Join(",", bbox.Select(coord => coord.ToString("F4", CultureInfo.InvariantCulture)))}]";


                var requestUrl =
                    $"{_urlMapboxStyles}{_usernameMapbox}/{_styleIdMapbox}/static/" +
                    $"{bboxString}/{width}x{height}@2x?" +
                    $"addlayer={encodedLayer}&padding={padding}&access_token={_accessToken}";

                using var client = new HttpClient();
                using var response = await client.GetAsync(requestUrl);

                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsByteArrayAsync();
                }
                else
                {
                    Console.WriteLine(response.Content.ReadAsByteArrayAsync());
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error getting static image: {ex.Message}", ex);
            }
        }

        static double[] CalculateBoundingBox(IEnumerable<double[]> coordinates)
        {
            double minLongitude = double.MaxValue;
            double minLatitude = double.MaxValue;
            double maxLongitude = double.MinValue;
            double maxLatitude = double.MinValue;

            foreach (var coord in coordinates)
            {
                if (coord.Length >= 2) 
                {
                    double longitude = coord[0];
                    double latitude = coord[1];

                    minLongitude = Math.Min(minLongitude, longitude);
                    minLatitude = Math.Min(minLatitude, latitude);
                    maxLongitude = Math.Max(maxLongitude, longitude);
                    maxLatitude = Math.Max(maxLatitude, latitude);
                }
            }

            return new double[] { minLongitude, minLatitude, maxLongitude, maxLatitude };
        }

        private static string GenerateRandomId(int length)
        {
            var random = new Random();
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var result = new StringBuilder(length);

            for (int i = 0; i < length; i++)
            {
                result.Append(validChars[random.Next(validChars.Length)]);
            }

            return result.ToString();
        }


        private static async Task ControlRateLimit(int maxRequestsPerMinute, int timeFrameInSeconds)
        {
            await _rateLimiter.WaitAsync();
            try
            {
                if (_requestCount >= maxRequestsPerMinute)
                {
                    var elapsedTime = (DateTime.UtcNow - _startTime).TotalSeconds;

                    if (elapsedTime < timeFrameInSeconds)
                    {
                        var waitTime = timeFrameInSeconds - elapsedTime;
                        Console.WriteLine($"Rate limit reached. Waiting {waitTime:F2} seconds...");
                        await Task.Delay((int)(waitTime * 1000));
                    }

                    _startTime = DateTime.UtcNow;
                    _requestCount = 0;
                }

                _requestCount++;
            }
            finally
            {
                _rateLimiter.Release();
            }
        }

        private static async Task DeleteTilesetAsync(string tilesetId)
        {
            var requestUrl = $"{_urlMapbox}{_usernameMapbox}.{tilesetId}?access_token={_accessToken}";

            const int maxRequestsPerMinute = 100;
            const int timeFrameInSeconds = 5;

            try
            {
                await ControlRateLimit(maxRequestsPerMinute, timeFrameInSeconds);
                using var client = new HttpClient();

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Delete,
                    RequestUri = new Uri(requestUrl)
                };

                using var response = await client.SendAsync(request);

                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Tileset '{tilesetId}' deleted successfully.");
                }
                else
                {
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while trying to delete the tileset '{tilesetId}': {ex.Message}", ex);
            }
        }

        private static async Task DeleteTilesetSourceAsync( string tilesetSourceId)
        {
            var requestUrl = $"{_urlMapbox}sources/{_usernameMapbox}/{tilesetSourceId}?access_token={_accessToken}";

            const int maxRequestsPerMinute = 100;
            const int timeFrameInSeconds = 5;

            try
            {
                await ControlRateLimit(maxRequestsPerMinute, timeFrameInSeconds);
                using var client = new HttpClient();

                var request = new HttpRequestMessage
                {
                    Method = HttpMethod.Delete,
                    RequestUri = new Uri(requestUrl)
                };

                using var response = await client.SendAsync(request);

                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    Console.WriteLine($"Tileset source '{tilesetSourceId}' deleted successfully.");
                }
                else
                {
                    throw new Exception($"{response.StatusCode} - {response.ReasonPhrase}");
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while trying to delete the tileset source '{tilesetSourceId}': {ex.Message}", ex);
            }
        }


    }


    public class SamplesDetailSaveDto
    {
        public double LongitudeInDegree { get; set; }
        public double LatitudeInDegree { get; set; }
    }
}