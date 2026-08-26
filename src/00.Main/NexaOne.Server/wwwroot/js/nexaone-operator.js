(function () {
    "use strict";

    function directTitle(section) {
        for (const child of section.children) {
            if (child.classList && child.classList.contains("layout-section-title")) return child;
        }
        return null;
    }

    function targetFor(root, region) {
        const titledSections = Array.from(root.querySelectorAll(".layout-section"))
            .filter(section => directTitle(section));

        if (region === "selection") return titledSections[0] || root.querySelector(".meta-search") || root;
        if (region === "execution") return titledSections[1] || titledSections[0] || root;
        if (region === "history") return titledSections[titledSections.length - 1] || root;
        return root;
    }

    window.nexaOperator = window.nexaOperator || {};
    window.nexaOperator.scrollToRegion = function (region) {
        const root = document.querySelector(".operator-runtime-frame .meta-screen");
        if (!root) return;

        const target = targetFor(root, region);
        const reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
        target.scrollIntoView({ behavior: reduceMotion ? "auto" : "smooth", block: "start", inline: "nearest" });
        target.classList.add("operator-scroll-target");
        window.setTimeout(function () { target.classList.remove("operator-scroll-target"); }, 900);
    };
}());
