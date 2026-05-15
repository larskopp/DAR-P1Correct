using Microsoft.Data.Sqlite;

string connectionString = "Data Source=autompg.sqlite";
using var connection = new SqliteConnection(connectionString);
connection.Open();

var drop = connection.CreateCommand();
drop.CommandText = "DROP TABLE IF EXISTS autompg";
drop.ExecuteNonQuery();

string sql = File.ReadAllText("autompg.sql");
var command = connection.CreateCommand();
command.CommandText = sql;
command.ExecuteNonQuery();
Console.WriteLine("Database loaded!");

var countCommand = connection.CreateCommand();
countCommand.CommandText = "SELECT COUNT(*) FROM autompg";
var countReader = countCommand.ExecuteReader();
countReader.Read();
int totalRows = int.Parse(countReader.GetValue(0).ToString());
Console.WriteLine($"Total records: {totalRows}");

string metaConnectionString = "Data Source=metadb.sqlite";
using var metaConnection = new SqliteConnection(metaConnectionString);
metaConnection.Open();
File.WriteAllText("metadb.txt",
    "CREATE TABLE idf_scores (attribute TEXT, value TEXT, idf REAL, qf REAL);\n" +
    "CREATE TABLE idf_scores_numeral (row_id INTEGER, attribute TEXT, value REAL, idf REAL);\n");
File.WriteAllText("metaload.txt", "");
Console.WriteLine("Metadb connection successful!");

var dropMetaCommand = metaConnection.CreateCommand();
dropMetaCommand.CommandText = "DROP TABLE IF EXISTS idf_scores";
dropMetaCommand.ExecuteNonQuery();

var dropNumeralCommand = metaConnection.CreateCommand();
dropNumeralCommand.CommandText = "DROP TABLE IF EXISTS idf_scores_numeral";
dropNumeralCommand.ExecuteNonQuery();

var tableCommand = metaConnection.CreateCommand();
tableCommand.CommandText = "CREATE TABLE idf_scores (attribute TEXT, value TEXT, idf REAL, qf REAL)";
tableCommand.ExecuteNonQuery();

var numericTableCommand = metaConnection.CreateCommand();
numericTableCommand.CommandText = "CREATE TABLE idf_scores_numeral (row_id INTEGER, attribute TEXT, value REAL, idf REAL)";
numericTableCommand.ExecuteNonQuery(); 

List<string> categoricalAtts = new List<string> { "brand", "model", "type", "origin", "cylinders" };
foreach (string att in categoricalAtts)
{
    var categoryCommand = connection.CreateCommand();
    categoryCommand.CommandText = $"SELECT {att}, COUNT(*) AS count FROM autompg GROUP BY {att} ORDER BY count DESC";
    var categoryReader = categoryCommand.ExecuteReader();
    while (categoryReader.Read())
    {
        double IDF = Math.Log(totalRows / int.Parse(categoryReader.GetValue(1).ToString()));
        var insertCommand = metaConnection.CreateCommand();
        insertCommand.CommandText = "INSERT INTO idf_scores (attribute, value, idf) VALUES (@attribute, @value, @idf)";
        insertCommand.Parameters.AddWithValue("@attribute", att);
        insertCommand.Parameters.AddWithValue("@value", categoryReader.GetValue(0).ToString());
        insertCommand.Parameters.AddWithValue("@idf", IDF);
        insertCommand.ExecuteNonQuery();
        File.AppendAllText("metaload.txt", $"INSERT INTO idf_scores (attribute, value, idf) VALUES ('{att}', '{categoryReader.GetValue(0)}', {IDF});\n");
    }
}

List<string> numericAtts = new List<string> { "mpg", "displacement", "horsepower", "weight", "acceleration", "model_year" };
foreach (string att in numericAtts)
{
    var numericalCommand = connection.CreateCommand();
    numericalCommand.CommandText = $"SELECT id, {att} FROM autompg";
    var numericalReader = numericalCommand.ExecuteReader();
    List<double> values = new List<double>();
    List<int> ids = new List<int>();
    while (numericalReader.Read())
    {
        ids.Add(Convert.ToInt32(numericalReader.GetValue(0)));
        values.Add(Convert.ToDouble(numericalReader.GetValue(1)));
    }
    double mean = values.Average();
    double sumSquaredDiffs = values.Sum(ti => (ti - mean) * (ti - mean));
    double standardDeviation = Math.Sqrt(sumSquaredDiffs / values.Count);
    double h = 1.06 * standardDeviation * Math.Pow(values.Count, -0.2);
    Console.WriteLine($"Attribute: {att}, Mean: {mean}, Std Dev: {standardDeviation}, h:{h}, Min: {values.Min()}, Max: {values.Max()}, Count: {values.Count()}");
    Console.WriteLine($"Attribute: {att}, Min: {values.Min()}, Max: {values.Max()}, Count: {values.Count()}");
    for (int i = 0; i < values.Count; i++)
    {
        double t = values[i];
        int rowId = ids[i];
        double sum = values.Sum(ti => Math.Exp(-(t - ti) * (t - ti) / (2 * h * h)));
        double idf = Math.Log(values.Count / sum);
        var insertCommand = metaConnection.CreateCommand();
        insertCommand.CommandText = "INSERT INTO idf_scores_numeral (row_id, attribute, value, idf) VALUES (@row_id, @attribute, @value, @idf)";
        insertCommand.Parameters.AddWithValue("@row_id", rowId);
        insertCommand.Parameters.AddWithValue("@attribute", att);
        insertCommand.Parameters.AddWithValue("@value", t);
        insertCommand.Parameters.AddWithValue("@idf", idf);
        insertCommand.ExecuteNonQuery();
        File.AppendAllText("metaload.txt", $"INSERT INTO idf_scores_numeral (row_id, attribute, value, idf) VALUES ({rowId}, '{att}', {t}, {idf});\n");
    }

}

string[] lines = File.ReadAllLines("workload.txt");
Dictionary<(string, string), int> qfCounts = new Dictionary<(string, string), int>();
foreach (string line in lines)
{
    if (!line.Contains(" times: ")) continue;
    string[] firstSplit = line.Split(" times: ");
    int timesCount = int.Parse(firstSplit[0]);
    string[] whereSplit = firstSplit[1].Split(" WHERE ");
    string conditionsPart = whereSplit[1];
    
    string[] pairs = conditionsPart.Split(" AND ");
    foreach (string pair in pairs)
    {
        if (pair.Contains(" IN "))
        {
            string[] inSplit = pair.Split(" IN ");
            string inAttribute = inSplit[0].Trim();
            string valuesPart = inSplit[1].Trim().Trim('(', ')');
            string[] inValues = valuesPart.Split(",");
            foreach (string inValue in inValues)
            {
                var inKey = (inAttribute, inValue.Trim('\''));
                if (!qfCounts.ContainsKey(inKey))
                    qfCounts[inKey] = 0;
                qfCounts[inKey] += timesCount;
            }
            continue;
        }
        string[] attValue = pair.Split(" = ");
        string attribute = attValue[0].Trim();
        string value = attValue[1].Trim().Trim('\'');
        var key = (attribute, value);
        if (!qfCounts.ContainsKey(key))
            qfCounts[key] = 0;
        qfCounts[key] += timesCount;
    }
}

int maxCount = qfCounts.Values.Max();
foreach (var kv in qfCounts)
{
    string attribute = kv.Key.Item1;
    string value = kv.Key.Item2;
    double qf = (double)kv.Value / maxCount;

    if (!categoricalAtts.Contains(attribute)) continue;

    var updateCommand = metaConnection.CreateCommand();
    updateCommand.CommandText = "UPDATE idf_scores SET qf = @qf WHERE attribute = @attribute AND value = @value";
    updateCommand.Parameters.AddWithValue("@qf", qf);
    updateCommand.Parameters.AddWithValue("@attribute", attribute);
    updateCommand.Parameters.AddWithValue("@value", value);
    updateCommand.ExecuteNonQuery();
    File.AppendAllText("metaload.txt", $"UPDATE idf_scores SET qf = {qf} WHERE attribute = '{attribute}' AND value = '{value}';\n");
}
var nullUpdateCommand = metaConnection.CreateCommand();
nullUpdateCommand.CommandText = "UPDATE idf_scores SET qf = 0 WHERE qf IS NULL";
nullUpdateCommand.ExecuteNonQuery();

Console.WriteLine("Preprocessing done!");