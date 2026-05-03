using UnityEngine;

public class SmoothingPanelOpener : MonoBehaviour
{
    public Animator animator;
    public bool isOpen;

    void Start()
    {
        isOpen = false;
        ClosePanel();
    }

    void OpenPanel()
    {
        animator.SetBool("isOpen", true);
    }

    void ClosePanel()
    {
        animator.SetBool("isOpen", false);
    }

    public void Interact()
    {
        isOpen = !isOpen;
        if (isOpen)
        {
            OpenPanel();
        }
        else
        {
            ClosePanel();
        }
    }
}