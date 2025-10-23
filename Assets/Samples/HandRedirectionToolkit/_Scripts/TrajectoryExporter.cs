using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System;

public class TrajectoryExporter : MonoBehaviour
{
    private List<Tuple<string, Vector3>> positions;
    public string fileName = "trajectory.csv";
    public string saveLocation = "Assets"; // ������ "ProjectRoot", "Documents" �����Զ���·��

    void Start()
    {
        positions = new List<Tuple<string, Vector3>>();
        StartCoroutine(RecordPositions()); // ʹ��Э��ÿ���¼�������
    }

    IEnumerator RecordPositions()
    {
        while (true)
        {
            RecordPosition();
            yield return new WaitForSeconds(0.2f); // ÿ0.2���¼һ�����ݣ�ÿ���¼5��
        }
    }

    void RecordPosition()
    {
        string currentTime = DateTime.Now.ToString("HH:mm:ss.fff");
        Vector3 currentPosition = transform.position;
        positions.Add(new Tuple<string, Vector3>(currentTime, currentPosition));

        // �����Console
        Debug.Log($"Time: {currentTime}, Position: ({currentPosition.x * 100} cm, {currentPosition.y * 100} cm, {currentPosition.z * 100} cm)");
    }

    void OnDestroy()
    {
        ExportToCSV();
    }

    void ExportToCSV()
    {
        string filePath = GetSavePath();
        using (StreamWriter writer = new StreamWriter(filePath))
        {
            writer.WriteLine("Time, X (cm), Y (cm), Z (cm)");
            foreach (var entry in positions)
            {
                string time = entry.Item1;
                Vector3 position = entry.Item2;
                writer.WriteLine($"{time}, {position.x * 100}, {position.y * 100}, {position.z * 100}");
            }
        }
        Debug.Log($"Trajectory data exported to {filePath}");
    }

    string GetSavePath()
    {
        switch (saveLocation)
        {
            case "ProjectRoot":
                return Path.Combine(Directory.GetParent(Application.dataPath).FullName, fileName);
            case "Documents":
                return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), fileName);
            default:
                return Path.Combine(Application.dataPath, fileName);
        }
    }
}
