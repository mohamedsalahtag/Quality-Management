---
app_name: "Sharbatly QMS — Arrival & Quality Order Guide"
version: "1.0"
purpose: "A step-by-step guide to the two everyday jobs in Sharbatly QMS: creating and completing an Arrival, and running a Quality Order from first sample to finished PDF report."
audiences: ["Operators", "Supervisors"]
site_url: "http://192.168.3.17:5244"
base_url: "http://192.168.3.17:5244"
roles: ["Operator", "Supervisor"]
theme: "default"
formats: ["html", "pdf", "docx", "pptx"]
lang: "en"
direction: "ltr"
login:
  url: "/Account/Login"
  username_field: "Username"
  password_field: "Password"
  submit_text: "Sign in"
---

# Sharbatly QMS — Arrival & Quality Order Guide

## 1. Overview

This short guide covers the two main jobs you do every day in Sharbatly QMS:

1. **The Arrival** — turn a container that SAP says has arrived into an inspection record: fill in the seal, temperatures and photos, then open a Quality Order.
2. **The Quality Order (QO)** — inspect samples from that container, record every defect and measurement, add photos, send it to a supervisor to Finish, and produce the printed **PDF report**.

The two jobs run one after the other: **an Arrival is completed first, and the Quality Order is opened from it.** Everything you inspect on the floor is recorded against the Quality Order.

> **Quick Start** — New here? Do these four things in order and you've done the whole job: **① pick your container from Pending → ② fill the checklist and Save → ③ Create Quality Order → ④ add your samples, then Submit for review.** The rest of this guide explains each step.

---

## 2. Getting started

### Signing in

- **Who uses it:** Operators and Supervisors
- **Screenshot:** `screenshots/account-login.png`

**Steps**
1. Open the app in your web browser: **http://192.168.3.17:5244**.
2. Type your **Username** and **Password** (the same ones you use to log in to your work computer).
3. Click **Sign in**.

**What you'll see next:** the home dashboard, with the top menu across the top of the page.

> **Tip** — If you see "Invalid credentials", check the username is just your name (for example `ahmed.ali`) with no `@` and no domain in front. If it still won't let you in, ask an administrator to check your account.

---

## 3. The Arrival process

An **Arrival** is one container of fruit. You create it from the list of containers SAP has told the app about, fill in what you see at the cold store, and then open a Quality Order on it.

### Step 1 — Pick your container from Pending

- **Who uses it:** Operators
- **Screenshot:** `screenshots/arrivals-pending.png`

**Steps**
1. Click **Pending Containers** in the top menu.
2. Find your container in the list. If it isn't there yet, click **Retrieve latest containers** at the top — a small status line tells you when the fresh list has come back from SAP.
3. If the list is long, type the container number, BOL, or PO in the filter boxes to narrow it down.
4. Click the **row** of the container you want.

**What you'll see next:** the app creates the Arrival and opens its details page, with an empty checklist ready to fill in.

> **Tip** — Each container becomes one Arrival. If you can't find a container, it usually just hasn't been fetched from SAP yet — click **Retrieve latest containers** and wait a few seconds.

### Step 2 — Fill and save the container checklist

- **Who uses it:** Operators
- **Screenshot:** `screenshots/arrivals-details.png`

**Steps**
1. On the Arrival details page, fill in the **seal number** and tick **Seal intact** if it was.
2. Tick **External damage** if there was any visible damage to the container.
3. Type the **discharge date** (the day the container was discharged) next to the arrival date.
4. Type the three **pulp temperatures** — front, middle and back.
5. If there is a **data logger**, type its serial number and tick the box to say its photo was taken.
6. Add a short **note** if anything is unusual.
7. Add photos with **Add photo** — you can drag a picture in, browse for a file, or take one with the camera.
8. Click **Save**.

**What you'll see next:** a green banner confirms the checklist saved. Your entries stay on the page.

> **Tip** — You don't have to finish in one sitting. The checklist saves every time you click **Save**, so you can come back and add more later.

### Step 3 — Complete the arrival and open a Quality Order

- **Who uses it:** Operators
- **Screenshot:** `screenshots/arrivals-details.png`

**Steps**
1. When the checklist is complete, click **Complete** (or **Create Quality Order**) at the top of the Arrival.
2. Confirm if the app asks you to.

**What you'll see next:** a brand-new **Quality Order** opens, with the material lines already filled in from SAP. You're now ready to start inspecting — that's the next section.

> **Tip** — A Quality Order can only be opened once the Arrival is **Completed**. If the button is greyed out, finish and Save the checklist first.

---

## 4. The Quality Order process

The **Quality Order (QO)** is where you record the inspection: one or more **samples**, the **defects** and **readings** on each, and the **photos**. When you're done you send it to a supervisor to **Finish**, and you can print the **PDF report**.

### Step 1 — Add a sample

- **Who uses it:** Operators
- **Screenshot:** `screenshots/qualityorders-sample.png`

**Steps**
1. On the Quality Order, click **Add sample**.
2. Choose the **Sample scope** — **One Carton** or **Multi-Carton**.
3. Check the **Sample size** — it fills in automatically from the material, but you can change it if you counted a different number.
4. Fill in the sample details you have (grower, pallet, etc.) if your team records them.
5. Click **Save sample**.

**What you'll see next:** the new sample appears on the Quality Order with its size and scope. You can now add the defects and readings for it.

> **Tip** — Made a mistake? Open the sample, change the values and **Save** again. The first Save creates the sample; every Save after that updates it. You can delete a sample only while the Quality Order is still **Open**.

### Step 2 — Record readings and defects

- **Who uses it:** Operators
- **Screenshot:** `screenshots/qualityorders-sample.png`

**Steps**
1. Open the sample you want to record against.
2. Type the **readings** you measured — Brix, firmness and so on. The list only shows the readings that apply to this kind of fruit.
3. Type the **count** for each **defect** you found. Leave a defect at zero if you didn't see it.
4. Add **photos** of the defects — drag, browse, or use the camera. The photo count on the sample updates straight away.
5. Click **Save sample**.

**What you'll see next:** the sample's defect summary and photo count update on the Quality Order.

> **Warning** — The total of all your defect counts on a sample can't be more than the sample size. If you hit the limit, the form tells you — recount or correct a number before saving.

### Step 3 — Complete the material header

- **Who uses it:** Operators
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Look at the **Material header** button on the Quality Order — it glows **yellow** until the header details are complete.
2. Click it and fill in the required header fields for the material.
3. Click **Save**.

**What you'll see next:** the button stops glowing once the header is complete. You need this done before you can submit.

### Step 4 — Submit for review

- **Who uses it:** Operators
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Make sure every sample you meant to inspect is saved, and the **Material header** is complete.
2. Click **Submit for review** at the top of the Quality Order.
3. Type a short note to the supervisor if you'd like.
4. Click **Confirm**.

**What you'll see next:** the Quality Order status becomes **Submitted**. You can't edit the samples any more until the supervisor either Finishes it or sends it back to you.

> **Tip** — If you try to submit before adding samples, the app warns you and asks you to confirm with a reason. That's there to stop empty orders going through by accident.

### Step 5 — Finish the Quality Order (Supervisor)

- **Who uses it:** Supervisors
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Open a **Submitted** Quality Order.
2. Review every sample and the operator's note.
3. If everything is correct, click **Finish** — the Quality Order becomes **Closed**.
4. If something needs fixing, click **Cancel submission** — the order goes back to **Open** so the operator can correct it.

**What you'll see next:** once Finished, the order is **Closed** and the report buttons unlock.

### Step 6 — Generate the PDF report

- **Who uses it:** Supervisors (and Operators who need a copy)
- **Screenshot:** `screenshots/qualityorders-details.png`

**Steps**
1. Open the finished (Closed) Quality Order.
2. Click **Download PDF** (or **Quality Report**) at the top.
3. The report opens as a PDF — it shows the shipment details, the **Time Bar**, every sample with its readings and defects, and the photos.
4. Save or print it as you need.
5. To e-mail it to the supplier, click **Send report to supplier** instead — the message is pre-filled; edit it if you want and click **Send**.

**What you'll see next:** a professional PDF quality report you can save, print, or e-mail. A green banner confirms if you sent it to the supplier.

> **Tip** — You can regenerate the PDF any time; it always reflects the latest saved data on the Quality Order.

---

## 5. Troubleshooting & FAQ

**I can't find my container in Pending Containers.**
It probably hasn't been fetched from SAP yet. Click **Retrieve latest containers** at the top of the Pending page and wait for the status line to say it's done, then look again.

**The "Create Quality Order" button is greyed out.**
The Arrival has to be **Completed** first. Finish the checklist, click **Save**, then **Complete** the arrival.

**The form won't let me save my defects.**
The total of all defect counts on a sample can't be larger than the sample size. Recount, or lower a number, then save.

**I can't edit a sample any more.**
Once a Quality Order is **Submitted**, samples are locked. Ask a supervisor to **Cancel submission** — that returns it to **Open** so you can edit again.

**The Material header button is glowing yellow.**
That means the header isn't complete yet. Click it, fill in the fields, and **Save**. You can't submit until it's done.

**How do I print the report?**
Open the finished Quality Order and click **Download PDF**. The report opens in your browser where you can save or print it.

---

## 6. Glossary

- **Arrival** — one container of fruit, from the moment it arrives until its inspection is recorded.
- **Checklist** — the seal, temperature, logger and photo record you fill in on the Arrival.
- **Quality Order (QO)** — the inspection for an arrival: its samples, defects, readings and photos.
- **Sample** — one inspected unit (a carton, or a set of cartons) within a Quality Order.
- **Reading** — a measurement such as Brix or firmness.
- **Defect** — a fault you count on a sample (for example bruising or decay).
- **Material header** — the material-level details that must be completed before a Quality Order can be submitted.
- **Submitted** — the state a Quality Order is in after an operator sends it for review; samples are locked.
- **Closed / Finished** — the state after a supervisor accepts the Quality Order; the report can now be produced.
- **Time Bar** — the number of days from the discharge (or arrival) date to the day the Quality Order was finished, shown on the report.
