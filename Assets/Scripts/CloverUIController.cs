using UnityEngine;
using UnityEngine.UI;

public class CloverUIController : MonoBehaviour
{
    [Header("Center Button")]
    [SerializeField] private Button startButton;
    
    [Header("Clover Buttons")]
    [SerializeField] private Button topButton;
    [SerializeField] private Button rightButton;
    [SerializeField] private Button bottomButton;
    [SerializeField] private Button leftButton;
    
    [Header("Button Images")]
    [SerializeField] private Image startButtonImage;
    [SerializeField] private Image topButtonImage;
    [SerializeField] private Image rightButtonImage;
    [SerializeField] private Image bottomButtonImage;
    [SerializeField] private Image leftButtonImage;
    
    [Header("Button Sprites")]
    [SerializeField] private Sprite startButtonDefaultSprite;
    [SerializeField] private Sprite startButtonPressedSprite;
    [SerializeField] private Sprite topButtonDefaultSprite;
    [SerializeField] private Sprite topButtonPressedSprite;
    [SerializeField] private Sprite rightButtonDefaultSprite;
    [SerializeField] private Sprite rightButtonPressedSprite;
    [SerializeField] private Sprite bottomButtonDefaultSprite;
    [SerializeField] private Sprite bottomButtonPressedSprite;
    [SerializeField] private Sprite leftButtonDefaultSprite;
    [SerializeField] private Sprite leftButtonPressedSprite;
    
    // Track button states
    private bool isStartButtonPressed = false;
    private bool isTopButtonPressed = false;
    private bool isRightButtonPressed = false;
    private bool isBottomButtonPressed = false;
    private bool isLeftButtonPressed = false;
    
    void Start()
    {
        SetupButtonCallbacks();
    }
    
    private void SetupButtonCallbacks()
    {
        // Start button - functionality can be assigned in inspector
        if (startButton != null)
        {
            startButton.onClick.AddListener(() => OnStartButtonPressed());
        }
        
        // Clover buttons - replace with your desired functionality
        if (topButton != null)
            topButton.onClick.AddListener(() => OnTopButtonPressed());
        
        if (rightButton != null)
            rightButton.onClick.AddListener(() => OnRightButtonPressed());
        
        if (bottomButton != null)
            bottomButton.onClick.AddListener(() => OnBottomButtonPressed());
        
        if (leftButton != null)
            leftButton.onClick.AddListener(() => OnLeftButtonPressed());
    }
    
    private void OnStartButtonPressed()
    {
        Debug.Log("Start button pressed");
        ToggleButtonIcon(startButtonImage, ref isStartButtonPressed, startButtonDefaultSprite, startButtonPressedSprite);
        // Add your functionality here
    }
    
    // Replace these methods with your desired functionality
    private void OnTopButtonPressed()
    {
        Debug.Log("Top clover button pressed");
        ToggleButtonIcon(topButtonImage, ref isTopButtonPressed, topButtonDefaultSprite, topButtonPressedSprite);
        // Add your functionality here
    }
    
    private void OnRightButtonPressed()
    {
        Debug.Log("Right clover button pressed");
        ToggleButtonIcon(rightButtonImage, ref isRightButtonPressed, rightButtonDefaultSprite, rightButtonPressedSprite);
        // Add your functionality here
    }
    
    private void OnBottomButtonPressed()
    {
        Debug.Log("Bottom clover button pressed");
        ToggleButtonIcon(bottomButtonImage, ref isBottomButtonPressed, bottomButtonDefaultSprite, bottomButtonPressedSprite);
        // Add your functionality here
    }
    
    private void OnLeftButtonPressed()
    {
        Debug.Log("Left clover button pressed");
        ToggleButtonIcon(leftButtonImage, ref isLeftButtonPressed, leftButtonDefaultSprite, leftButtonPressedSprite);
        // Add your functionality here
    }
    
    // Method to toggle button icon between default and pressed states
    private void ToggleButtonIcon(Image buttonImage, ref bool isPressed, Sprite defaultSprite, Sprite pressedSprite)
    {
        if (buttonImage == null) return;
        
        isPressed = !isPressed;
        buttonImage.sprite = isPressed ? pressedSprite : defaultSprite;
    }
    
    // Method to set button to specific state (useful for resetting or programmatic control)
    private void SetButtonIcon(Image buttonImage, ref bool isPressed, bool newState, Sprite defaultSprite, Sprite pressedSprite)
    {
        if (buttonImage == null) return;
        
        isPressed = newState;
        buttonImage.sprite = isPressed ? pressedSprite : defaultSprite;
    }
    
    // Public methods to control button states from other scripts
    public void SetStartButtonState(bool pressed)
    {
        SetButtonIcon(startButtonImage, ref isStartButtonPressed, pressed, startButtonDefaultSprite, startButtonPressedSprite);
    }
    
    public void SetTopButtonState(bool pressed)
    {
        SetButtonIcon(topButtonImage, ref isTopButtonPressed, pressed, topButtonDefaultSprite, topButtonPressedSprite);
    }
    
    public void SetRightButtonState(bool pressed)
    {
        SetButtonIcon(rightButtonImage, ref isRightButtonPressed, pressed, rightButtonDefaultSprite, rightButtonPressedSprite);
    }
    
    public void SetBottomButtonState(bool pressed)
    {
        SetButtonIcon(bottomButtonImage, ref isBottomButtonPressed, pressed, bottomButtonDefaultSprite, bottomButtonPressedSprite);
    }
    
    public void SetLeftButtonState(bool pressed)
    {
        SetButtonIcon(leftButtonImage, ref isLeftButtonPressed, pressed, leftButtonDefaultSprite, leftButtonPressedSprite);
    }
    
    // Reset all buttons to default state
    public void ResetAllButtons()
    {
        SetStartButtonState(false);
        SetTopButtonState(false);
        SetRightButtonState(false);
        SetBottomButtonState(false);
        SetLeftButtonState(false);
    }
    
    void OnDestroy()
    {
        // Clean up button listeners to prevent memory leaks
        if (startButton != null)
            startButton.onClick.RemoveAllListeners();
        
        if (topButton != null)
            topButton.onClick.RemoveAllListeners();
        
        if (rightButton != null)
            rightButton.onClick.RemoveAllListeners();
        
        if (bottomButton != null)
            bottomButton.onClick.RemoveAllListeners();
        
        if (leftButton != null)
            leftButton.onClick.RemoveAllListeners();
    }
    
    // Method to update button sprites when you have your PNGs ready
    public void UpdateButtonSprites(Sprite startSprite, Sprite topSprite, Sprite rightSprite, Sprite bottomSprite, Sprite leftSprite)
    {
        if (startButtonImage != null && startSprite != null)
            startButtonImage.sprite = startSprite;
        
        if (topButtonImage != null && topSprite != null)
            topButtonImage.sprite = topSprite;
        
        if (rightButtonImage != null && rightSprite != null)
            rightButtonImage.sprite = rightSprite;
        
        if (bottomButtonImage != null && bottomSprite != null)
            bottomButtonImage.sprite = bottomSprite;
        
        if (leftButtonImage != null && leftSprite != null)
            leftButtonImage.sprite = leftSprite;
    }
}