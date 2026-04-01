using UnityEngine;
using UnityEngine.SceneManagement;

public class SwitchScene : MonoBehaviour
{
    [SerializeField] private string sceneName;

    public void NextScene()
    {
        SceneManager.LoadScene(sceneName);
    }
}