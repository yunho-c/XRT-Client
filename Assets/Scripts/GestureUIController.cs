using UnityEngine;
using Oculus.Interaction;

public class GestureUIController : MonoBehaviour
{
    [SerializeField] private GameObject uiPanel; // Reference to your CanvasRoot
    [SerializeField] private bool toggleMode = true; // Toggle on/off or show while gesture active
    
    [Header("Audio")]
    [SerializeField] private AudioSource audioSource;
    [SerializeField] private AudioClip gestureDetectedSound;
    [SerializeField] private AudioClip gestureLostSound; // Optional

    private bool isUIVisible = true; // UI visible by default

    public void OnGestureDetected()
    {
        // Play sound when gesture is detected
        PlaySound(gestureDetectedSound);
        
        if (toggleMode)
        {
            // Toggle UI visibility
            isUIVisible = !isUIVisible;
            uiPanel.SetActive(isUIVisible);
        }
        else
        {
            // Show UI while gesture is active
            uiPanel.SetActive(true);
        }

        // Always show UI when gesture is detected
        // uiPanel.SetActive(true);
    }

    public void OnGestureLost()
    {
        // Optionally play sound when gesture is lost
        if (gestureLostSound != null)
        {
            PlaySound(gestureLostSound);
        }
        
        if (!toggleMode)
        {
            // Hide UI when gesture is no longer detected
            uiPanel.SetActive(false);
        }

        // Always hide UI when gesture is lost
        // uiPanel.SetActive(false);
    }

    private void PlaySound(AudioClip clip)
    {
        if (audioSource != null && clip != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }

    // Optional: Public methods for more complex UI transitions
    public void ShowUI()
    {
        uiPanel.SetActive(true);
        isUIVisible = true;
    }

    public void HideUI()
    {
        uiPanel.SetActive(false);
        isUIVisible = false;
    }
}