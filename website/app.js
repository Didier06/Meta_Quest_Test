document.addEventListener('DOMContentLoaded', () => {
    // --- Configuration ---
    const BASE_TOPIC_IN = "FABLAB_21_22/Unity/metaquest/in"; // Generic Prefabs
    const PENDULE_TOPIC_IN = "FABLAB_21_22/Unity/Pendule/in";
    const PENDULE_COUPLES_TOPIC_IN = "FABLAB_21_22/Unity/PendulesCouples/in";

    let client = null;

    // --- Elements ---
    const mqttForm = document.getElementById('mqttForm');
    const statusEl = document.getElementById('mqttStatus');
    const logEl = document.getElementById('mqttLog');
    const btnConnect = document.getElementById('btnConnect');
    const btnDisconnect = document.getElementById('btnDisconnect');
    const alertBox = document.getElementById('connectionAlert');


    // --- UI Helpers ---
    function log(msg) {
        const time = new Date().toLocaleTimeString();
        logEl.textContent += `[${time}] ${msg}\n`;
        logEl.scrollTop = logEl.scrollHeight;
    }

    function setConnectedState(isConnected) {
        if (isConnected) {
            statusEl.textContent = 'Connecté';
            statusEl.className = 'badge bg-success';
            btnConnect.disabled = true;
            btnDisconnect.disabled = false;
            if (alertBox) alertBox.classList.add('d-none');
            // Disable inputs while connected
            document.getElementById('mqttHost').disabled = true;
            document.getElementById('mqttPort').disabled = true;
            document.getElementById('mqttUser').disabled = true;
            document.getElementById('mqttPass').disabled = true;
        } else {
            statusEl.textContent = 'Déconnecté';
            statusEl.className = 'badge bg-secondary';
            btnConnect.disabled = false;
            // Only disable disconnect if client is truly null/ended
            btnDisconnect.disabled = (!client || !client.connected);
            if (alertBox) alertBox.classList.remove('d-none');

            document.getElementById('mqttHost').disabled = false;
            document.getElementById('mqttPort').disabled = false;
            document.getElementById('mqttUser').disabled = false;
            document.getElementById('mqttPass').disabled = false;
        }
    }

    // --- MQTT Connection ---
    mqttForm.addEventListener('submit', (e) => {
        e.preventDefault();

        if (client && client.connected) {
            return;
        }
        // Force cleanup if existing client exists but confused state
        if (client) {
            client.end();
            client = null;
        }

        const host = document.getElementById('mqttHost').value.trim();
        const port = parseInt(document.getElementById('mqttPort').value.trim());
        const user = document.getElementById('mqttUser').value.trim();
        const pass = document.getElementById('mqttPass').value;

        if (!host || !port) return alert("Hôte et Port requis.");

        // Enforce password presence
        if (!pass) {
            alert("Veuillez saisir votre mot de passe MQTT avant de vous connecter.");
            return;
        }

        const protocol = (port === 443 || port === 8083) ? 'wss' : 'ws';
        const url = `${protocol}://${host}:${port}`;

        // ClientID logic: username + random
        const safeUser = user || 'WebCtrl';
        const clientId = `${safeUser}_${Math.random().toString(16).slice(2, 8)}`;

        const options = {
            clientId: clientId,
            username: user,
            password: pass,
            keepalive: 60,
            reconnectPeriod: 5000 // Retry every 5s if fail
        };

        log(`Connexion à ${url}...`);

        try {
            client = mqtt.connect(url, options);

            client.on('connect', () => {
                log('✅ Connecté au Broker !');
                setConnectedState(true);

                // Subscribe to Pendulum Data
                client.subscribe([PENDULE_TOPIC_OUT, PENDULE_COUPLES_TOPIC_OUT], (err) => {
                    if (!err) log(`Abonné aux données pendules`);
                });
            });

            client.on('message', (topic, message) => {
                const msgStr = message.toString();

                if (topic === PENDULE_TOPIC_OUT) {
                    try {
                        const data = JSON.parse(msgStr);
                        // Expecting: { temps: 12.3, angle: 45.0 }
                        if (data.temps !== undefined && data.angle !== undefined) {
                            if (penduleChart) {
                                penduleChart.data.labels.push(data.temps);
                                penduleChart.data.datasets[0].data.push(data.angle);

                                // Keep last 100 points
                                if (penduleChart.data.labels.length > 100) {
                                    penduleChart.data.labels.shift();
                                    penduleChart.data.datasets[0].data.shift();
                                }
                                penduleChart.update('none'); // Efficient update
                            }
                        }
                    } catch (e) {
                        // Silent fail for perf or log if needed
                    }
                }
                else if (topic === PENDULE_COUPLES_TOPIC_OUT) {
                    try {
                        // Expecting: { temps: 12.3, theta1: 10, theta2: 20 }
                        const data = JSON.parse(msgStr);
                        if (data.temps !== undefined) {
                            if (coupledChart) {
                                coupledChart.data.labels.push(data.temps);

                                if (data.theta1 !== undefined) coupledChart.data.datasets[0].data.push(data.theta1);
                                if (data.theta2 !== undefined) coupledChart.data.datasets[1].data.push(data.theta2);

                                if (coupledChart.data.labels.length > 100) {
                                    coupledChart.data.labels.shift();
                                    coupledChart.data.datasets[0].data.shift();
                                    coupledChart.data.datasets[1].data.shift();
                                }
                                coupledChart.update('none');
                            }
                        }
                    } catch (e) { }
                }
            });

            client.on('reconnect', () => {
                log('🔄 Tentative de reconnexion...');
                statusEl.textContent = 'Reconnexion...';
            });

            client.on('offline', () => {
                log('⚠️ Offline / Perte de connexion.');
            });

            client.on('error', (err) => {
                log('❌ Erreur MQTT: ' + err.message);
                // Do not nullify client here, allow reconnect or manual disconnect
            });

            client.on('close', () => {
                // This fires on every disconnect (including temp ones during reconnect loop)
                // We should NOT set client = null here automatically if we want auto-reconnect.
                // But we should update UI.
                if (statusEl.textContent !== 'Reconnexion...') {
                    log('🔌 Connexion fermée.');
                }
                // Don't disable the "Disconnect" button completely so user can kill the loop
                btnDisconnect.disabled = false;
            });

        } catch (err) {
            log('Exception : ' + err.message);
        }
    });

    btnDisconnect.addEventListener('click', () => {
        if (client) {
            log('🛑 Déconnexion manuelle demandée.');
            client.end(true); // Force close
            client = null;
            setConnectedState(false);
            btnDisconnect.disabled = true;
        }
    });

    // --- Helper: Publish ---
    function publish(topic, payloadObj) {
        if (!client || !client.connected) {
            alert("Erreur: Non connecté au MQTT.");
            return;
        }
        const json = JSON.stringify(payloadObj);
        client.publish(topic, json);
        log(`Envoyé vers ${topic} : ${json}`);
    }

    // --- Tab 2: Pendules ---

    // Initialisation Graphique
    let penduleChart;
    const ctx = document.getElementById('penduleChart').getContext('2d');

    penduleChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [{
                label: 'Angle (°)',
                data: [],
                borderColor: '#0dcaf0', // Info color
                borderWidth: 2,
                pointRadius: 0,
                tension: 0.1,
                fill: false
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false, // Performance
            plugins: {
                legend: { display: false }
            },
            scales: {
                x: { display: false }, // Focus on waveform
                y: {
                    grid: { color: 'rgba(255,255,255,0.1)' },
                    ticks: { color: '#ccc' }
                }
            }
        }
    });

    // Initialisation Graphique (Couplé)
    let coupledChart;
    const ctxCoupled = document.getElementById('coupledChart').getContext('2d');

    coupledChart = new Chart(ctxCoupled, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'Theta 1 (°)',
                    data: [],
                    borderColor: '#ffc107', // Warning color (Yellow)
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.1,
                    fill: false
                },
                {
                    label: 'Theta 2 (°)',
                    data: [],
                    borderColor: '#fd7e14', // Orange
                    borderWidth: 2,
                    pointRadius: 0,
                    tension: 0.1,
                    fill: false
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            plugins: {
                legend: { display: true, labels: { color: '#ccc' } } // Legend needed here
            },
            scales: {
                x: { display: false },
                y: {
                    grid: { color: 'rgba(255,255,255,0.1)' },
                    ticks: { color: '#ccc' }
                }
            }
        }
    });

    // Subscriptions
    const PENDULE_TOPIC_OUT = "FABLAB_21_22/Unity/Pendule/out";
    const PENDULE_COUPLES_TOPIC_OUT = "FABLAB_21_22/Unity/PendulesCouples/out";

    // Simple Pendulum
    document.getElementById('btnSendSimple').addEventListener('click', () => {
        const m = parseFloat(document.getElementById('p_m').value);
        const alpha = parseFloat(document.getElementById('p_alpha').value);
        const fs = parseFloat(document.getElementById('p_fs').value);
        const angle = parseFloat(document.getElementById('p_angle').value);

        const payload = {};
        if (!isNaN(m)) payload.m = m;
        if (!isNaN(alpha)) payload.alpha = alpha;
        if (!isNaN(fs)) payload.fs = fs;
        if (!isNaN(angle)) payload.angle_init = angle;

        if (Object.keys(payload).length === 0) {
            alert("Veuillez remplir au moins un champ.");
            return;
        }

        publish(PENDULE_TOPIC_IN, payload);
    });

    // Coupled Pendulums
    document.getElementById('btnSendCoupled').addEventListener('click', () => {
        const th1 = parseFloat(document.getElementById('c_th1').value);
        const th2 = parseFloat(document.getElementById('c_th2').value);
        const C = parseFloat(document.getElementById('c_C').value);
        const f = parseFloat(document.getElementById('c_f').value);
        const m1 = parseFloat(document.getElementById('c_m1').value);
        const m2 = parseFloat(document.getElementById('c_m2').value);

        const payload = {};
        if (!isNaN(th1)) payload.th1_i = th1;
        if (!isNaN(th2)) payload.th2_i = th2;
        if (!isNaN(C)) payload.C = C;
        if (!isNaN(f)) payload.f = f;
        if (!isNaN(m1)) payload.m1 = m1;
        if (!isNaN(m2)) payload.m2 = m2;

        if (Object.keys(payload).length === 0) {
            alert("Veuillez remplir au moins un champ.");
            return;
        }

        publish(PENDULE_COUPLES_TOPIC_IN, payload);
    });

    // --- Tab 3: Prefabs & Three.js Visualization ---

    let threeScene, threeCamera, threeRenderer;
    let world; // Cannon.js Physics World
    let placedObjects3D = {}; // Dictionary { name: { mesh: ..., body: ... } }
    let isThreeInit = false;
    let gravityActive = false;
    let fbxLoader = null;

    function initThreeJS() {
        if (isThreeInit) return;

        const container = document.getElementById('three-container');
        if (!container) return;

        // --- 1. Physics World (Cannon.js) ---
        world = new CANNON.World();
        world.gravity.set(0, -9.82, 0); // Earth gravity
        world.broadphase = new CANNON.NaiveBroadphase();
        world.solver.iterations = 10;

        // Physics Materials
        const groundMat = new CANNON.Material();
        const objectMat = new CANNON.Material();
        const contactMat = new CANNON.ContactMaterial(groundMat, objectMat, {
            friction: 0.5, restitution: 0.3
        });
        world.addContactMaterial(contactMat);

        // Ground Plane (Physics)
        const groundBody = new CANNON.Body({
            mass: 0, // Static
            shape: new CANNON.Plane(),
            material: groundMat
        });
        groundBody.quaternion.setFromAxisAngle(new CANNON.Vec3(1, 0, 0), -Math.PI / 2); // Rotate to be flat XZ
        world.addBody(groundBody);


        // --- 2. Visual Scene (Three.js) ---
        threeScene = new THREE.Scene();
        threeScene.background = new THREE.Color(0x000000);

        threeCamera = new THREE.PerspectiveCamera(50, container.clientWidth / container.clientHeight, 0.1, 100);
        threeCamera.position.set(8, 8, 12);
        threeCamera.lookAt(0, 2.5, 0);

        threeRenderer = new THREE.WebGLRenderer({ antialias: true });
        threeRenderer.setSize(container.clientWidth, container.clientHeight);
        container.appendChild(threeRenderer.domElement);

        if (typeof THREE.OrbitControls !== 'undefined') {
            const controls = new THREE.OrbitControls(threeCamera, threeRenderer.domElement);
            controls.enableDamping = true;
            controls.dampingFactor = 0.05;
        }
        // Lights (Needed for FBX)
        const ambientLight = new THREE.AmbientLight(0xffffff, 0.6);
        threeScene.add(ambientLight);
        const dirLight = new THREE.DirectionalLight(0xffffff, 0.8);
        dirLight.position.set(5, 10, 7);
        threeScene.add(dirLight);

        if (typeof THREE.FBXLoader !== 'undefined') {
            fbxLoader = new THREE.FBXLoader();
            fbxLoader.setResourcePath('models/'); // Help find textures like PenduleColor.png
        }

        const gridHelper = new THREE.GridHelper(20, 20, 0x444444, 0x222222);
        threeScene.add(gridHelper);

        const geometryVol = new THREE.BoxGeometry(5, 5, 10);
        const edges = new THREE.EdgesGeometry(geometryVol);
        const VolumeBox = new THREE.LineSegments(edges, new THREE.LineBasicMaterial({ color: 0x00ff00 }));
        VolumeBox.position.set(0, 2.5, 0);
        threeScene.add(VolumeBox);

        const axesHelper = new THREE.AxesHelper(2);
        threeScene.add(axesHelper);

        // Animation Loop
        const timeStep = 1 / 60;
        function animate() {
            requestAnimationFrame(animate);

            if (gravityActive && world) {
                world.step(timeStep);

                // Sync Visuals with Physics
                Object.values(placedObjects3D).forEach(obj => {
                    if (obj.mesh && obj.body) {
                        obj.mesh.position.copy(obj.body.position);
                        obj.mesh.quaternion.copy(obj.body.quaternion);
                    }
                });
            }

            threeRenderer.render(threeScene, threeCamera);
        }
        animate();

        window.addEventListener('resize', () => {
            if (container && threeCamera && threeRenderer) {
                threeCamera.aspect = container.clientWidth / container.clientHeight;
                threeCamera.updateProjectionMatrix();
                threeRenderer.setSize(container.clientWidth, container.clientHeight);
            }
        });

        isThreeInit = true;
    }

    // Listener for Dropdown Selection to populate Input
    // (Consolidated logic for prefabSelect is handled at the end of the file or here, ensuring only one declaration)
    const prefabSelect = document.getElementById('prefabSelect');
    if (prefabSelect) {
        prefabSelect.addEventListener('change', () => {
            if (prefabSelect.value) {
                document.getElementById('targetName').value = prefabSelect.value;
            }
        });
    }

    function updateOrAddObject3D(name, pos, rot, scale) {
        if (!threeScene || !world) return;

        // Handle "Cube" generic input mapping to Cube1/Cube2 if needed, or rely on exact tracking.
        // User might type "Cube", let's leave it as individual tracker.

        let container = placedObjects3D[name];

        // --- Create New Object ---
        if (!container) {
            const nameLower = name.toLowerCase();
            let shape, mass = 1;

            // 1. Determine Physics Shape
            if (nameLower.includes('sphere')) {
                const radius = 0.5 * scale.x;
                shape = new CANNON.Sphere(radius);
                mass = 5;
            } else {
                // Cubes, Stirling, Pendulums -> Box Physics (Simplified)
                const halfExtents = new CANNON.Vec3(0.5 * scale.x, 0.5 * scale.y, 0.5 * scale.z);
                shape = new CANNON.Box(halfExtents);

                // Heavier mass for complex objects
                if (nameLower.includes('stirlin') || nameLower.includes('pendule')) {
                    mass = 20;
                }
            }

            // 2. Physics Body
            const body = new CANNON.Body({
                mass: mass,
                shape: shape,
                position: new CANNON.Vec3(pos.x, pos.y, pos.z)
            });
            body.quaternion.setFromEuler(
                THREE.Math.degToRad(rot.x),
                THREE.Math.degToRad(rot.y),
                THREE.Math.degToRad(rot.z)
            );
            world.addBody(body);

            // 3. Visual Mesh (Placeholder)
            const placeholderGeo = nameLower.includes('sphere') ?
                new THREE.SphereGeometry(0.5 * scale.x, 32, 16) :
                new THREE.BoxGeometry(scale.x, scale.y, scale.z);

            const material = new THREE.MeshStandardMaterial({
                color: 0x0088ff, // Blue placeholder 
                transparent: true,
                opacity: 0.5,
                wireframe: true
            });

            const mesh = new THREE.Mesh(placeholderGeo, material);
            threeScene.add(mesh);

            // 4. Load FBX if matches specific list
            // Check for known FBX names or patterns
            const isFbx = nameLower.includes('stirlin') || nameLower.includes('pendule') || nameLower.includes('fbx');

            if (fbxLoader && isFbx) {
                const fbxPath = `models/${name}.fbx`;
                console.log("Loading FBX:", fbxPath);

                fbxLoader.load(fbxPath, (fbx) => {
                    threeScene.remove(mesh);
                    const fbxScale = 0.01;
                    fbx.scale.set(fbxScale * scale.x, fbxScale * scale.y, fbxScale * scale.z);
                    placedObjects3D[name].mesh = fbx;
                    threeScene.add(fbx);
                }, undefined, (error) => {
                    console.warn(`Failed to load ${fbxPath}, use placeholder.`, error);
                    mesh.material.color.setHex(0xff0000); // Red if failed
                    mesh.material.wireframe = false;
                    mesh.material.opacity = 1;
                });
            }

            // Store Pair
            placedObjects3D[name] = { mesh: mesh, body: body };
        }
        // --- Update Existing (Teleport) ---
        else {
            const body = container.body;
            body.velocity.set(0, 0, 0);
            body.angularVelocity.set(0, 0, 0);

            body.position.set(pos.x, pos.y, pos.z);
            body.quaternion.setFromEuler(
                THREE.Math.degToRad(rot.x),
                THREE.Math.degToRad(rot.y),
                THREE.Math.degToRad(rot.z)
            );
        }
    }

    // Init Logic on Tab Show
    const tabPrefab = document.getElementById('prefab-tab');
    if (tabPrefab) {
        tabPrefab.addEventListener('shown.bs.tab', () => {
            // Slight delay to ensure DOM is fully rendered
            setTimeout(initThreeJS, 100);
        });
    }

    // Force resize check in loop (Hack for Bootstrap tabs)
    setInterval(() => {
        if (isThreeInit && threeRenderer && threeCamera) {
            const container = document.getElementById('three-container');
            if (container && container.clientWidth > 0 && container.clientWidth !== threeRenderer.domElement.width) {
                threeCamera.aspect = container.clientWidth / container.clientHeight;
                threeCamera.updateProjectionMatrix();
                threeRenderer.setSize(container.clientWidth, container.clientHeight);
            }
        }
    }, 500);

    document.getElementById('btnSendPrefab').addEventListener('click', () => {
        const targetName = document.getElementById('targetName').value.trim();
        if (!targetName) return alert("Nom de la cible requis.");

        const payload = { targetName: targetName };

        // Position
        const px = parseFloat(document.getElementById('posX').value);
        const py = parseFloat(document.getElementById('posY').value);
        const pz = parseFloat(document.getElementById('posZ').value);
        if (!isNaN(px) || !isNaN(py) || !isNaN(pz)) {
            payload.position = { x: px || 0, y: py || 0, z: pz || 0 };
        }

        // Rotation
        const rx = parseFloat(document.getElementById('rotX').value);
        const ry = parseFloat(document.getElementById('rotY').value);
        const rz = parseFloat(document.getElementById('rotZ').value);
        if (!isNaN(rx) || !isNaN(ry) || !isNaN(rz)) {
            payload.rotation = { x: rx || 0, y: ry || 0, z: rz || 0 };
        }

        // Scale
        const sx = parseFloat(document.getElementById('scaleX').value);
        const sy = parseFloat(document.getElementById('scaleY').value);
        const sz = parseFloat(document.getElementById('scaleZ').value);
        if (!isNaN(sx) || !isNaN(sy) || !isNaN(sz)) {
            payload.scale = { x: isNaN(sx) ? 1 : sx, y: isNaN(sy) ? 1 : sy, z: isNaN(sz) ? 1 : sz };
        }

        // Extra
        const rotSpeedY = parseFloat(document.getElementById('rotSpeedY').value);
        if (!isNaN(rotSpeedY)) {
            payload.rotationSpeed = { x: 0, y: rotSpeedY, z: 0 };
        }

        // Gravity
        const useGravity = document.getElementById('useGravity').checked;
        if (useGravity) {
            payload.useGravity = true;
        }
        // Update visual gravity state
        gravityActive = useGravity;

        publish(BASE_TOPIC_IN, payload);

        // Update 3D Visualization via Helper
        const scale = { x: isNaN(sx) ? 1 : sx, y: isNaN(sy) ? 1 : sy, z: isNaN(sz) ? 1 : sz };
        updateOrAddObject3D(targetName, { x: px || 0, y: py || 0, z: pz || 0 }, { x: rx || 0, y: ry || 0, z: rz || 0 }, scale);
    });

    // --- Tab 4: Jauges ---
    const btnSendGaugeOnly = document.getElementById('btnSendGaugeOnly');
    if (btnSendGaugeOnly) {
        btnSendGaugeOnly.addEventListener('click', () => {
            const target = document.getElementById('gaugeTarget').value.trim();
            const val = parseFloat(document.getElementById('gaugeValueInput').value);

            if (!target) return alert("Veuillez entrer un nom de cible.");
            if (isNaN(val)) return alert("Veuillez entrer une valeur numérique.");

            const payload = {
                targetName: target,
                gauge_value: val
            };

            publish(BASE_TOPIC_IN, payload);
        });
    }

    // --- Tab 5: Geolocalisation ---
    let map;
    let marker;
    const placedMarkers = {};
    const GEO_TOPIC_IN = "FABLAB_21_22/unity/testgps/in";

    // Red Icon Definition
    const redIcon = new L.Icon({
        iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-red.png',
        shadowUrl: 'https://cdnjs.cloudflare.com/ajax/libs/leaflet/0.7.7/images/marker-shadow.png',
        iconSize: [25, 41],
        iconAnchor: [12, 41],
        popupAnchor: [1, -34],
        shadowSize: [41, 41]
    });

    // Initialize map when tab is shown to fix rendering issues
    const tabGeo = document.getElementById('nav-geo-tab');
    if (tabGeo) {
        tabGeo.addEventListener('shown.bs.tab', () => {
            if (!map) {
                // Centered on Contes (Approx): 43.8135, 7.3145
                map = L.map('map').setView([43.8135, 7.3145], 18);

                // Satellite View (Esri World Imagery)
                L.tileLayer('https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}', {
                    attribution: 'Tiles &copy; Esri &mdash; Source: Esri, i-cubed, USDA, USGS, AEX, GeoEye, Getmapping, Aerogrid, IGN, IGP, UPR-EGP, and the GIS User Community',
                    maxZoom: 19
                }).addTo(map);

                // Click handler
                map.on('click', (e) => {
                    const lat = e.latlng.lat.toFixed(6);
                    const lon = e.latlng.lng.toFixed(6);

                    document.getElementById('geoLat').value = lat;
                    document.getElementById('geoLon').value = lon;

                    if (marker) map.removeLayer(marker);
                    marker = L.marker([lat, lon]).addTo(map);
                });

                // Try to locate user immediately
                map.locate({ setView: true, maxZoom: 19, enableHighAccuracy: true, watch: false });

                map.on('locationfound', (e) => {
                    if (!marker) {
                        marker = L.marker(e.latlng).addTo(map);
                        document.getElementById('geoLat').value = e.latlng.lat.toFixed(6);
                        document.getElementById('geoLon').value = e.latlng.lng.toFixed(6);
                    }
                    // Force view update just in case
                    map.setView(e.latlng, 19);
                });

                map.on('locationerror', (e) => {
                    console.warn("Geolocation failed: ", e.message);
                    // Fallback is already set to Contes in initialization
                });
            } else {
                map.invalidateSize(); // Fix gray map if hidden previously
            }
        });
    }

    // Place Object
    const btnPlaceGeo = document.getElementById('btnPlaceGeo');
    if (btnPlaceGeo) {
        btnPlaceGeo.addEventListener('click', () => {
            const name = document.getElementById('geoPrefab').value;
            const lat = parseFloat(document.getElementById('geoLat').value);
            const lon = parseFloat(document.getElementById('geoLon').value);
            const alt = parseFloat(document.getElementById('geoAlt').value) || 0;

            if (!name || isNaN(lat) || isNaN(lon)) return alert("Nom, Latitude et Longitude requis.");

            const payload = {
                items: [
                    {
                        name: name,
                        latitude: lat,
                        longitude: lon,
                        altitudeOffset: alt
                    }
                ]
            };

            publish(GEO_TOPIC_IN, payload);

            // UI: Create persistent marker
            if (placedMarkers[name]) {
                placedMarkers[name].setLatLng([lat, lon]);
                placedMarkers[name].altitudeOffset = alt; // Update stored altitude
            } else {
                const m = L.marker([lat, lon], { draggable: true, icon: redIcon }).addTo(map);
                m.altitudeOffset = alt; // Store altitude for dragging
                m.bindTooltip(name, { permanent: true, direction: 'top', className: 'fw-bold', offset: [0, -40] }).openTooltip();

                // Click Event: Select marker and populate inputs
                m.on('click', function (event) {
                    const marker = event.target;
                    const position = marker.getLatLng();

                    // Update Inputs
                    document.getElementById('geoPrefab').value = name;
                    document.getElementById('geoLat').value = position.lat.toFixed(6);
                    document.getElementById('geoLon').value = position.lng.toFixed(6);
                    document.getElementById('geoAlt').value = marker.altitudeOffset || 0;
                });

                // Drag Event: Update Unity when marker is moved
                m.on('dragend', function (event) {
                    const marker = event.target;
                    const position = marker.getLatLng();
                    const newLat = position.lat;
                    const newLng = position.lng;

                    // Update inputs for feedback
                    document.getElementById('geoLat').value = newLat.toFixed(6);
                    document.getElementById('geoLon').value = newLng.toFixed(6);

                    const dragPayload = {
                        items: [{
                            name: name,
                            latitude: newLat,
                            longitude: newLng,
                            altitudeOffset: marker.altitudeOffset || 0
                        }]
                    };
                    publish(GEO_TOPIC_IN, dragPayload);
                });

                placedMarkers[name] = m;
            }
        });
    }

    // Delete Object
    const btnDeleteGeo = document.getElementById('btnDeleteGeo');
    if (btnDeleteGeo) {
        btnDeleteGeo.addEventListener('click', () => {
            const name = document.getElementById('geoPrefab').value;
            if (!name) return alert("Nom de l'objet requis pour suppression.");

            const payload = {
                items: [
                    {
                        name: name,
                        delete: true
                    }
                ]
            };

            publish(GEO_TOPIC_IN, payload);

            // UI: Remove persistent marker
            if (placedMarkers[name]) {
                map.removeLayer(placedMarkers[name]);
                delete placedMarkers[name];
            } else {
                console.warn("No marker found to remove for:", name);
            }
        });
    }

    // Delete All Objects
    const btnDeleteAllGeo = document.getElementById('btnDeleteAllGeo');
    if (btnDeleteAllGeo) {
        btnDeleteAllGeo.addEventListener('click', () => {
            if (!confirm("Voulez-vous vraiment TOUT supprimer ?")) return;

            // Gather all names to delete
            const itemsToDelete = [];
            for (const name in placedMarkers) {
                itemsToDelete.push({ name: name, delete: true });
                map.removeLayer(placedMarkers[name]);
            }

            // Also add standard prefabs just in case they are not in placedMarkers but in the list (cleanup)
            const prefabOptions = document.getElementById('geoPrefab').options;
            for (let i = 0; i < prefabOptions.length; i++) {
                const n = prefabOptions[i].value;
                if (!placedMarkers[n]) {
                    itemsToDelete.push({ name: n, delete: true });
                }
            }

            if (itemsToDelete.length > 0) {
                const payload = { items: itemsToDelete };
                publish(GEO_TOPIC_IN, payload);
            }

            // Clear local storage logic
            for (const name in placedMarkers) delete placedMarkers[name];
        });
    }



    // --- Demo Presets ---
    window.fillPrefabDemo = function (type) {
        if (type === 'reset') {
            document.getElementById('posX').value = 0;
            document.getElementById('posY').value = 1;
            document.getElementById('posZ').value = 0;
            document.getElementById('rotX').value = 0;
            document.getElementById('rotY').value = 0;
            document.getElementById('rotZ').value = 0;
            document.getElementById('scaleX').value = 1;
            document.getElementById('scaleY').value = 1;
            document.getElementById('scaleZ').value = 1;
            document.getElementById('rotSpeedY').value = "";
            document.getElementById('gaugeVal').value = "";
        }
        else if (type === 'spin') {
            document.getElementById('rotSpeedY').value = 90;
        }
        else if (type === 'grow') {
            document.getElementById('scaleX').value = 2;
            document.getElementById('scaleY').value = 2;
            document.getElementById('scaleZ').value = 2;
        }
    };

    // (End of DOMContentLoaded)
});
