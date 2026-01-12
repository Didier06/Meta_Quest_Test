
## Configuration du Pendule (via MQTT)

Le pendule peut être piloté et configuré en temps réel via le topic `FABLAB_21_22/Unity/Pendule/in`.
Le message doit être au format JSON. Tous les paramètres sont optionnels et peuvent être envoyés ensemble ou séparément.

### Paramètres JSON

| Clé | Type | Unité / Plage | Description |
| :--- | :--- | :--- | :--- |
| **`angle_init`** | `float` | Degrés (-180 à +180) | Réinitialise la position du pendule. Déclenche une animation de transition douce (2s) avant de relâcher la physique. |
| **`m`** | `float` | kg (approx) | Définit la **Masse** du pendule (`Rigidbody.mass`). Une masse plus élevée augmente l'inertie. |
| **`alpha`** | `float` | Unity Linear Drag (0 à infini) | Définit le **Frottement Fluide** (résistance de l'air). <br>Note : Mappé sur `Rigidbody.linearDamping`. Plus la valeur est haute, plus le pendule freine proportionnellement à sa vitesse. Valeurs recommandées : 0.5 à 5 (oscillant), 10+ (très amorti). |
| **`fs`** | `float` | Couple (N.m) | Définit le **Frottement Solide** (sec) via un frein moteur sur l'axe (`HingeJoint`). <br>- Si `> 0` : Applique un couple de freinage constant. <br>- Si `0` : Désactive complètement le moteur (roue libre). |

### Exemples de Payload

* **Reset simple :**

    ```json
    { "angle_init": -45.0 }
    ```

* **Configuration Physique complète :**

    ```json
    {
      "m": 2.5,
      "alpha": 0.1,
      "fs": 0.5
    }
    ```

* **Reset avec changement de physique (Mega-Message) :**

    ```json
    {
      "angle_init": 90,
      "m": 1.0,
      "alpha": 20.0,  // Fort amortissement fluide
      "fs": 0         // Pas de frottement sec
    }
    ```
