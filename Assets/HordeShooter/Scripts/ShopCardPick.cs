using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// One shop card slot: an interactable pedestal AND a uGUI Button target.
/// Interact() handles the physical pedestal; _Pick() is the entry point bound
/// to the card panel's uGUI Button (Button.onClick -> SendCustomEvent("_Pick")).
/// Both forward the card index to the ShopController.
/// </summary>
public class ShopCardPick : UdonSharpBehaviour
{
    public ShopController shop;
    public int cardIndex;

    public override void Interact()
    {
        if (shop != null) shop._PickCard(cardIndex);
    }

    /// <summary>uGUI Button entry point (parameterless, exact name preserved).</summary>
    public void _Pick()
    {
        if (shop != null) shop._PickCard(cardIndex);
    }
}
